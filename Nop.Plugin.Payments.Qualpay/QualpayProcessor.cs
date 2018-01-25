using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Nop.Core;
using Nop.Core.Domain.Customers;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Core.Plugins;
using Nop.Plugin.Payments.Qualpay.Domain;
using Nop.Plugin.Payments.Qualpay.Domain.PaymentGateway;
using Nop.Plugin.Payments.Qualpay.Models;
using Nop.Plugin.Payments.Qualpay.Services;
using Nop.Plugin.Payments.Qualpay.Validators;
using Nop.Services.Catalog;
using Nop.Services.Common;
using Nop.Services.Configuration;
using Nop.Services.Customers;
using Nop.Services.Directory;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.Tax;

namespace Nop.Plugin.Payments.Qualpay
{
    /// <summary>
    /// Represents Qualpay payment gateway processor
    /// </summary>
    public class QualpayProcessor : BasePlugin, IPaymentMethod
    {
        #region Fields

        private readonly CurrencySettings _currencySettings;
        private readonly ICheckoutAttributeParser _checkoutAttributeParser;
        private readonly ICurrencyService _currencyService;
        private readonly ICustomerService _customerService;
        private readonly IGenericAttributeService _genericAttributeService;
        private readonly ILocalizationService _localizationService;
        private readonly ILogger _logger;
        private readonly IOrderTotalCalculationService _orderTotalCalculationService;
        private readonly IPaymentService _paymentService;
        private readonly IPriceCalculationService _priceCalculationService;
        private readonly IProductAttributeParser _productAttributeParser;
        private readonly ISettingService _settingService;
        private readonly ITaxService _taxService;
        private readonly IWebHelper _webHelper;
        private readonly QualpayManager _qualpayManager;
        private readonly QualpaySettings _qualpaySettings;

        #endregion

        #region Ctor

        public QualpayProcessor(CurrencySettings currencySettings,
            ICheckoutAttributeParser checkoutAttributeParser,
            ICurrencyService currencyService,
            ICustomerService customerService,
            IGenericAttributeService genericAttributeService,
            ILocalizationService localizationService,
            ILogger logger,
            IOrderTotalCalculationService orderTotalCalculationService,
            IPaymentService paymentService,
            IPriceCalculationService priceCalculationService,
            IProductAttributeParser productAttributeParser,
            ISettingService settingService,
            ITaxService taxService,
            IWebHelper webHelper,
            QualpayManager qualpayManager,
            QualpaySettings qualpaySettings)
        {
            this._currencySettings = currencySettings;
            this._checkoutAttributeParser = checkoutAttributeParser;
            this._currencyService = currencyService;
            this._customerService = customerService;
            this._genericAttributeService = genericAttributeService;
            this._localizationService = localizationService;
            this._logger = logger;
            this._orderTotalCalculationService = orderTotalCalculationService;
            this._paymentService = paymentService;
            this._priceCalculationService = priceCalculationService;
            this._productAttributeParser = productAttributeParser;
            this._settingService = settingService;
            this._taxService = taxService;
            this._webHelper = webHelper;
            this._qualpayManager = qualpayManager;
            this._qualpaySettings = qualpaySettings;
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Get transaction line items
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <param name="storeId">Store identifier</param>
        /// <param name="orderTotal">Order total</param>
        /// <param name="taxAmount">Tax amount</param>
        /// <returns>List of transaction items</returns>
        private IList<LineItem> GetItems(Customer customer, int storeId, decimal orderTotal, out decimal taxAmount)
        {
            var items = new List<LineItem>();

            //get current shopping cart
            var shoppingCart = customer.ShoppingCartItems
                .Where(shoppingCartItem => shoppingCartItem.ShoppingCartType == ShoppingCartType.ShoppingCart)
                .LimitPerStore(storeId).ToList();

            //set tax amount
            taxAmount = _orderTotalCalculationService.GetTaxTotal(shoppingCart);

            //create transaction items from shopping cart items
            items.AddRange(shoppingCart.Where(shoppingCartItem => shoppingCartItem.Product != null).Select(shoppingCartItem =>
            {
                //item price
                var price = _taxService.GetProductPrice(shoppingCartItem.Product, _priceCalculationService.GetUnitPrice(shoppingCartItem),
                    false, shoppingCartItem.Customer, out _);

                return CreateItem(price, shoppingCartItem.Product.Name,
                    shoppingCartItem.Product.FormatSku(shoppingCartItem.AttributesXml, _productAttributeParser), shoppingCartItem.Quantity);
            }));

            //create transaction items from checkout attributes
            var checkoutAttributesXml = customer
                .GetAttribute<string>(SystemCustomerAttributeNames.CheckoutAttributes, storeId);
            if (!string.IsNullOrEmpty(checkoutAttributesXml))
            {
                var attributeValues = _checkoutAttributeParser.ParseCheckoutAttributeValues(checkoutAttributesXml);
                items.AddRange(attributeValues.Where(attributeValue => attributeValue.CheckoutAttribute != null).Select(attributeValue =>
                {
                    return CreateItem(_taxService.GetCheckoutAttributePrice(attributeValue, false, customer),
                        $"{attributeValue.CheckoutAttribute.Name} ({attributeValue.Name})", "checkout");
                }));
            }

            //create transaction item for payment method additional fee
            var paymentAdditionalFee = _paymentService.GetAdditionalHandlingFee(shoppingCart, PluginDescriptor.SystemName);
            var paymentPrice = _taxService.GetPaymentMethodAdditionalFee(paymentAdditionalFee, false, customer);
            if (paymentPrice > decimal.Zero)
                items.Add(CreateItem(paymentPrice, $"Payment ({PluginDescriptor.FriendlyName})", "payment"));

            //create transaction item for shipping rate
            if (shoppingCart.RequiresShipping())
            {
                var shippingPrice = _orderTotalCalculationService.GetShoppingCartShippingTotal(shoppingCart, false);
                if (shippingPrice.HasValue && shippingPrice.Value > decimal.Zero)
                    items.Add(CreateItem(shippingPrice.Value, "Shipping rate", "shipping"));
            }

            //create transaction item for all discounts
            var amountDifference = orderTotal - items.Sum(lineItem => lineItem.UnitPrice * lineItem.Quantity) - taxAmount;
            if (amountDifference < decimal.Zero)
                items.Add(CreateItem(amountDifference, "Discount amount", "discounts"));

            return items;
        }

        /// <summary>
        /// Create transaction line item
        /// </summary>
        /// <param name="price">Price per unit</param>
        /// <param name="description">Item description</param>
        /// <param name="productCode">Item code (e.g. SKU)</param>
        /// <param name="quantity">Quntity</param>
        /// <returns>Transaction line item</returns>
        private LineItem CreateItem(decimal price, string description, string productCode, int quantity = 1)
        {
            return new LineItem
            {
                CreditType = ItemCreditType.Debit,
                Description = CommonHelper.EnsureMaximumLength(description, 25),
                MeasureUnit = "*",
                ProductCode = CommonHelper.EnsureMaximumLength(productCode, 12),
                Quantity = quantity,
                UnitPrice = price
            };
        }

        #endregion

        #region Methods

        /// <summary>
        /// Process a payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>Process payment result</returns>
        public ProcessPaymentResult ProcessPayment(ProcessPaymentRequest processPaymentRequest)
        {
            var customer = _customerService.GetCustomerById(processPaymentRequest.CustomerId)
                ?? throw new NopException("Customer cannot be loaded");

            //Qualpay Payment Gateway supports only USD currency
            var primaryStoreCurrency = _currencyService.GetCurrencyById(_currencySettings.PrimaryStoreCurrencyId);
            if (!primaryStoreCurrency.CurrencyCode.Equals("USD", StringComparison.InvariantCultureIgnoreCase))
                throw new NopException("USD is not primary store currency");

            //create request
            var transactionRequest = new TransactionRequest
            {
                //set order number, max length is 25 
                PurchaseId = CommonHelper.EnsureMaximumLength(processPaymentRequest.OrderGuid.ToString(), 25),
                Amount = Math.Round(processPaymentRequest.OrderTotal, 2),
                CurrencyIsoCode = QualpayDefaults.UsdNumericIsoCode
            };

            //set item lines
            transactionRequest.Items = GetItems(customer, processPaymentRequest.StoreId, processPaymentRequest.OrderTotal, out decimal taxAmount);

            //parse custom values
            var useStoredCardKey = _localizationService.GetResource("Plugins.Payments.Qualpay.UseStoredCard");
            var useStoredCard = processPaymentRequest.CustomValues.ContainsKey(useStoredCardKey) &&
                Convert.ToBoolean(processPaymentRequest.CustomValues[useStoredCardKey]);

            var saveCardKey = _localizationService.GetResource("Plugins.Payments.Qualpay.SaveCardDetails");
            var saveCard = processPaymentRequest.CustomValues.ContainsKey(saveCardKey) &&
                Convert.ToBoolean(processPaymentRequest.CustomValues[saveCardKey]);

            var cardId = customer.GetAttribute<string>("QualpayVaultCardId", _genericAttributeService, processPaymentRequest.StoreId);
            if (useStoredCard)
            {
                //customer has stored card and want to use it
                transactionRequest.CardId = cardId;
            }
            else
            {
                //or he sets card details
                transactionRequest.CardholderName = processPaymentRequest.CreditCardName;
                transactionRequest.CardNumber = processPaymentRequest.CreditCardNumber;
                transactionRequest.Cvv2 = processPaymentRequest.CreditCardCvv2;
                transactionRequest.ExpirationDate = $"{processPaymentRequest.CreditCardExpireMonth:D2}{processPaymentRequest.CreditCardExpireYear.ToString().Substring(2)}";
                transactionRequest.AvsAddress = CommonHelper.EnsureMaximumLength(customer.BillingAddress?.Address1, 20);
                transactionRequest.AvsZip = customer.BillingAddress?.ZipPostalCode;

                //save or update credit card details in Qualpay Vault
                if (saveCard)
                {
                    transactionRequest.IsTokenize = true;

                    //and customer details if not exist
                    if (string.IsNullOrEmpty(cardId))
                    {
                        transactionRequest.CustomerId = customer.Id.ToString();
                        transactionRequest.Customer = new PaymentGatewayCustomer
                        {
                            Email = customer.BillingAddress?.Email,
                            FirstName = customer.BillingAddress?.FirstName,
                            LastName = customer.BillingAddress?.LastName,
                            Phone = customer.BillingAddress?.PhoneNumber
                        };
                    }
                }
            }

            //get response
            var response = 
                _qualpaySettings.PaymentTransactionType == TransactionType.Authorization ? _qualpayManager.Authorize(transactionRequest) :
                _qualpaySettings.PaymentTransactionType == TransactionType.Sale ? _qualpayManager.Sale(transactionRequest) : 
                throw new NopException("Request type is not supported");

            //request succeeded
            var result = new ProcessPaymentResult
            {
                AvsResult = response.AvsResult,
                AuthorizationTransactionCode = response.AuthorizationCode
            };

            //set an authorization details
            if (_qualpaySettings.PaymentTransactionType == TransactionType.Authorization)
            {
                result.AuthorizationTransactionId = response.TransactionId;
                result.AuthorizationTransactionResult = response.ResponseMessage;
                result.NewPaymentStatus = PaymentStatus.Authorized;
            }

            //or set a capture details
            if (_qualpaySettings.PaymentTransactionType == TransactionType.Sale)
            {   
                result.CaptureTransactionId = response.TransactionId;
                result.CaptureTransactionResult = response.ResponseMessage;
                result.NewPaymentStatus = PaymentStatus.Paid;
            }

            //save Qualpay Vault card ID
            if (saveCard)
                _genericAttributeService.SaveAttribute(customer, "QualpayVaultCardId", response.CardId, processPaymentRequest.StoreId);

            return result;
        }

        /// <summary>
        /// Post process payment (used by payment gateways that require redirecting to a third-party URL)
        /// </summary>
        /// <param name="postProcessPaymentRequest">Payment info required for an order processing</param>
        public void PostProcessPayment(PostProcessPaymentRequest postProcessPaymentRequest)
        {
            //nothing
        }

        /// <summary>
        /// Returns a value indicating whether payment method should be hidden during checkout
        /// </summary>
        /// <param name="cart">Shoping cart</param>
        /// <returns>true - hide; false - display.</returns>
        public bool HidePaymentMethod(IList<ShoppingCartItem> cart)
        {
            //you can put any logic here
            //for example, hide this payment method if all products in the cart are downloadable
            //or hide this payment method if current customer is from certain country
            return false;
        }

        /// <summary>
        /// Gets additional handling fee
        /// </summary>
        /// <param name="cart">Shoping cart</param>
        /// <returns>Additional handling fee</returns>
        public decimal GetAdditionalHandlingFee(IList<ShoppingCartItem> cart)
        {
            var result = this.CalculateAdditionalFee(_orderTotalCalculationService, cart,
                _qualpaySettings.AdditionalFee, _qualpaySettings.AdditionalFeePercentage);

            return result;
        }

        /// <summary>
        /// Captures payment
        /// </summary>
        /// <param name="capturePaymentRequest">Capture payment request</param>
        /// <returns>Capture payment result</returns>
        public CapturePaymentResult Capture(CapturePaymentRequest capturePaymentRequest)
        {
            //capture full amount of the authorized transaction
            var captureResponse = _qualpayManager.CaptureTransaction(new CaptureRequest
            {
                TransactionId = capturePaymentRequest.Order.AuthorizationTransactionId,
                Amount = Math.Round(capturePaymentRequest.Order.OrderTotal, 2)
            });

            //request succeeded
            return new CapturePaymentResult
            {
                CaptureTransactionId = captureResponse.TransactionId,
                CaptureTransactionResult = captureResponse.ResponseMessage,
                NewPaymentStatus = PaymentStatus.Paid
            };
        }

        /// <summary>
        /// Refunds a payment
        /// </summary>
        /// <param name="refundPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public RefundPaymentResult Refund(RefundPaymentRequest refundPaymentRequest)
        {
            //refund full or partial amount of the captured transaction
            var refundResponse = _qualpayManager.Refund(new RefundRequest
            {
                TransactionId = refundPaymentRequest.Order.CaptureTransactionId,
                Amount = Math.Round(refundPaymentRequest.AmountToRefund, 2)
            });

            //request succeeded
            return new RefundPaymentResult
            {
                NewPaymentStatus = refundPaymentRequest.IsPartialRefund ? PaymentStatus.PartiallyRefunded : PaymentStatus.Refunded
            };
        }

        /// <summary>
        /// Voids a payment
        /// </summary>
        /// <param name="voidPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public VoidPaymentResult Void(VoidPaymentRequest voidPaymentRequest)
        {
            //void full amount of the authorized transaction
            var voidResponse = _qualpayManager.VoidTransaction(new VoidRequest
            {
                TransactionId = voidPaymentRequest.Order.AuthorizationTransactionId
            });

            //request succeeded
            return new VoidPaymentResult
            {
                NewPaymentStatus = PaymentStatus.Voided
            };
        }

        /// <summary>
        /// Process recurring payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>Process payment result</returns>
        public ProcessPaymentResult ProcessRecurringPayment(ProcessPaymentRequest processPaymentRequest)
        {
            return new ProcessPaymentResult { Errors = new[] { "Recurring payment not supported" } };
        }

        /// <summary>
        /// Cancels a recurring payment
        /// </summary>
        /// <param name="cancelPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public CancelRecurringPaymentResult CancelRecurringPayment(CancelRecurringPaymentRequest cancelPaymentRequest)
        {
            return new CancelRecurringPaymentResult { Errors = new[] { "Recurring payment not supported" } };
        }

        /// <summary>
        /// Gets a value indicating whether customers can complete a payment after order is placed but not completed (for redirection payment methods)
        /// </summary>
        /// <param name="order">Order</param>
        /// <returns>Result</returns>
        public bool CanRePostProcessPayment(Order order)
        {
            if (order == null)
                throw new ArgumentNullException(nameof(order));
            
            //let's ensure that at least 5 seconds passed after order is placed
            //P.S. there's no any particular reason for that. we just do it
            if ((DateTime.UtcNow - order.CreatedOnUtc).TotalSeconds < 5)
                return false;

            return true;
        }

        /// <summary>
        /// Validate payment form
        /// </summary>
        /// <param name="form">The parsed form values</param>
        /// <returns>List of validating errors</returns>
        public IList<string> ValidatePaymentForm(IFormCollection form)
        {
            var warnings = new List<string>();

            //validate
            var validator = new PaymentInfoValidator(_localizationService);
            var model = new PaymentInfoModel
            {
                CardholderName = form["CardholderName"],
                CardNumber = form["CardNumber"],
                CardCode = form["CardCode"],
                ExpireMonth = form["ExpireMonth"],
                ExpireYear = form["ExpireYear"]
            };

            //don't validate card details on using stored card
            var useStoredCard = false;
            if (form.Keys.Contains("UseStoredCard"))
                bool.TryParse(form["UseStoredCard"][0], out useStoredCard);
            model.UseStoredCard = useStoredCard;

            var validationResult = validator.Validate(model);
            if (!validationResult.IsValid)
                warnings.AddRange(validationResult.Errors.Select(error => error.ErrorMessage));

            return warnings;
        }

        /// <summary>
        /// Get payment information
        /// </summary>
        /// <param name="form">The parsed form values</param>
        /// <returns>Payment info holder</returns>
        public ProcessPaymentRequest GetPaymentInfo(IFormCollection form)
        {
            var paymentRequest = new ProcessPaymentRequest
            {
                CreditCardName = form["CardholderName"],
                CreditCardNumber = form["CardNumber"],
                CreditCardExpireMonth = int.Parse(form["ExpireMonth"]),
                CreditCardExpireYear = int.Parse(form["ExpireYear"]),
                CreditCardCvv2 = form["CardCode"]
            };

            //pass custom values to payment processor
            if (form.Keys.Contains("SaveCardDetails") && bool.TryParse(form["SaveCardDetails"][0], out bool saveCardDetails) && saveCardDetails)
                paymentRequest.CustomValues.Add(_localizationService.GetResource("Plugins.Payments.Qualpay.SaveCardDetails"), true);

            if (form.Keys.Contains("UseStoredCard") && bool.TryParse(form["UseStoredCard"][0], out bool useStoredCard) && useStoredCard)
                paymentRequest.CustomValues.Add(_localizationService.GetResource("Plugins.Payments.Qualpay.UseStoredCard"), true);

            return paymentRequest;
        }

        /// <summary>
        /// Gets a configuration page URL
        /// </summary>
        public override string GetConfigurationPageUrl()
        {
            return $"{_webHelper.GetStoreLocation()}Admin/Qualpay/Configure";
        }

        /// <summary>
        /// Gets a view component for displaying plugin in public store ("payment info" checkout step)
        /// </summary>
        /// <param name="viewComponentName">View component name</param>
        public void GetPublicViewComponent(out string viewComponentName)
        {
            viewComponentName = QualpayDefaults.ViewComponentName;
        }
        
        /// <summary>
        /// Install the plugin
        /// </summary>
        public override void Install()
        {
            //settings
            _settingService.SaveSetting(new QualpaySettings
            {
                UseSandbox = true,
                PaymentTransactionType = TransactionType.Sale
            });

            //locales
            this.AddOrUpdatePluginLocaleResource("Enums.QualpayRequestType.Authorization", "Authorization");
            this.AddOrUpdatePluginLocaleResource("Enums.QualpayRequestType.Sale", "Sale (authorization and capture)");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.Fields.AdditionalFee", "Additional fee");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.Fields.AdditionalFee.Hint", "Enter additional fee to charge your customers.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.Fields.AdditionalFeePercentage", "Additional fee. Use percentage");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.Fields.AdditionalFeePercentage.Hint", "Determines whether to apply a percentage additional fee to the order total. If not enabled, a fixed value is used.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.Fields.MerchantId", "Merchant ID");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.Fields.MerchantId.Hint", "Specify your Qualpay merchant identifier.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.Fields.PaymentTransactionType", "Payment transaction type");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.Fields.PaymentTransactionType.Hint", "Choose payment transaction type");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.Fields.SecurityKey", "Security key");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.Fields.SecurityKey.Hint", "Specify your Qualpay payment gateway security key.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.Fields.UseSandbox", "Use Sandbox");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.Fields.UseSandbox.Hint", "Check to enable sandbox (testing environment).");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.PaymentMethodDescription", "Pay by credit / debit card using Qualpay payment gateway");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.SaveCardDetails", "Save the card data to Qualpay Vault for next time");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.Qualpay.UseStoredCard", "Use a previously saved card");

            base.Install();
        }

        /// <summary>
        /// Uninstall the plugin
        /// </summary>
        public override void Uninstall()
        {
            //settings
            _settingService.DeleteSetting<QualpaySettings>();

            //locales
            this.DeletePluginLocaleResource("Enums.QualpayRequestType.Authorization");
            this.DeletePluginLocaleResource("Enums.QualpayRequestType.Sale");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.Fields.AdditionalFee");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.Fields.AdditionalFee.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.Fields.AdditionalFeePercentage");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.Fields.AdditionalFeePercentage.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.Fields.MerchantId");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.Fields.MerchantId.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.Fields.PaymentTransactionType");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.Fields.PaymentTransactionType.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.Fields.SecurityKey");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.Fields.SecurityKey.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.Fields.UseSandbox");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.Fields.UseSandbox.Hint");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.PaymentMethodDescription");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.SaveCardDetails");
            this.DeletePluginLocaleResource("Plugins.Payments.Qualpay.UseStoredCard");

            base.Uninstall();
        }

        #endregion

        #region Properies

        /// <summary>
        /// Gets a value indicating whether capture is supported
        /// </summary>
        public bool SupportCapture
        {
            get { return true; }
        }

        /// <summary>
        /// Gets a value indicating whether partial refund is supported
        /// </summary>
        public bool SupportPartiallyRefund
        {
            get { return true; }
        }

        /// <summary>
        /// Gets a value indicating whether refund is supported
        /// </summary>
        public bool SupportRefund
        {
            get { return true; }
        }

        /// <summary>
        /// Gets a value indicating whether void is supported
        /// </summary>
        public bool SupportVoid
        {
            get { return true; }
        }

        /// <summary>
        /// Gets a recurring payment type of payment method
        /// </summary>
        public RecurringPaymentType RecurringPaymentType
        {
            get { return RecurringPaymentType.NotSupported; }
        }

        /// <summary>
        /// Gets a payment method type
        /// </summary>
        public PaymentMethodType PaymentMethodType
        {
            get { return PaymentMethodType.Standard; }
        }

        /// <summary>
        /// Gets a value indicating whether we should display a payment information page for this plugin
        /// </summary>
        public bool SkipPaymentInfo
        {
            get { return false; }
        }
        
        /// <summary>
        /// Gets a payment method description that will be displayed on checkout pages in the public store
        /// </summary>
        public string PaymentMethodDescription
        {
            //return description of this payment method to be display on "payment method" checkout step. good practice is to make it localizable
            //for example, for a redirection payment method, description may be like this: "You will be redirected to Transaction site to complete the payment"
            get { return _localizationService.GetResource("Plugins.Payments.Qualpay.PaymentMethodDescription"); }
        }

        #endregion
    }
}