using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Nop.Core;
using Nop.Plugin.Payments.Qualpay.Domain;
using Nop.Plugin.Payments.Qualpay.Domain.PaymentGateway;
using Nop.Plugin.Payments.Qualpay.Domain.Platform;
using Nop.Services.Logging;

namespace Nop.Plugin.Payments.Qualpay.Services
{
    /// <summary>
    /// Represents the Qualpay manager
    /// </summary>
    public class QualpayManager
    {
        #region Fields

        private readonly ILogger _logger;
        private readonly IWorkContext _workContext;
        private readonly QualpaySettings _qualpaySettings;

        #endregion

        #region Ctor

        public QualpayManager(ILogger logger,
            IWorkContext workContext,
            QualpaySettings qualpaySettings)
        {
            this._logger = logger;
            this._workContext = workContext;
            this._qualpaySettings = qualpaySettings;
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Get the Qualpay service base URL
        /// </summary>
        /// <returns>URL</returns               
        private string GetServiceBaseUrl()
        {
            return _qualpaySettings.UseSandbox ? "https://api-test.qualpay.com/" : "https://api.qualpay.com/";
        }

        /// <summary>
        /// Process Qualpay Platform request
        /// </summary>
        /// <typeparam name="TRequest">Request type</typeparam>
        /// <typeparam name="TResponse">Response type</typeparam>
        /// <param name="platformRequest">Request</param>
        /// <returns>Response</returns>
        private TResponse ProcessPlatformRequest<TRequest, TResponse>(TRequest platformRequest)
            where TRequest : PlatformRequest where TResponse : PlatformResponse
        {
            return HandleRequestAction(() =>
            {
                //process request
                var response = ProcessRequest<TRequest, TResponse>(platformRequest)
                    ?? throw new NopException("An error occurred while processing. Error details in the log.");

                //whether request is succeeded
                if (response.ResponseCode != PlatformResponseCode.Success)
                    throw new NopException($"{response.ResponseCode}. {response.Message}");

                return response;
            });
        }

        /// <summary>
        /// Process Qualpay Payment Gateway request
        /// </summary>
        /// <typeparam name="TRequest">Request type</typeparam>
        /// <typeparam name="TResponse">Response type</typeparam>
        /// <param name="paymentGatewayRequest">Request</param>
        /// <returns>Response</returns>
        private TResponse ProcessPaymentGatewayRequest<TRequest, TResponse>(TRequest paymentGatewayRequest)
            where TRequest : PaymentGatewayRequest where TResponse : PaymentGatewayResponse
        {
            var response = HandleRequestAction(() =>
            {
                //set credentials
                paymentGatewayRequest.DeveloperId = QualpayDefaults.DeveloperId;
                paymentGatewayRequest.MerchantId = long.Parse(_qualpaySettings.MerchantId);

                //process request
                return ProcessRequest<TRequest, TResponse>(paymentGatewayRequest);
            }) ?? throw new NopException("An error occurred while processing. Error details in the log.");

            //whether request is succeeded
            if (response.ResponseCode != PaymentGatewayResponseCode.Success)
                throw new NopException($"{response.ResponseCode}. {response.Message}");

            return response;
        }

        /// <summary>
        /// Process request
        /// </summary>
        /// <typeparam name="TRequest">Request type</typeparam>
        /// <typeparam name="TResponse">Response type</typeparam>
        /// <param name="request">Request</param>
        /// <returns>Response</returns>
        private TResponse ProcessRequest<TRequest, TResponse>(TRequest request) where TRequest: QualpayRequest where TResponse: QualpayResponse
        {
            //create requesting URL
            var url = $"{GetServiceBaseUrl()}{request.GetRequestPath()}";

            //create web request
            var webRequest = (HttpWebRequest)WebRequest.Create(url);
            webRequest.Method = request.GetRequestMethod();
            webRequest.UserAgent = QualpayDefaults.UserAgent;
            webRequest.Accept = "application/json";
            webRequest.ContentType = "application/json; charset=utf-8";

            //add authorization header
            var encodedSecurityKey = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_qualpaySettings.SecurityKey}:"));
            webRequest.Headers.Add(HttpRequestHeader.Authorization, $"Basic {encodedSecurityKey}");

            //create post data
            if (request.GetRequestMethod() != WebRequestMethods.Http.Get)
            {
                var postData = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(request));
                webRequest.ContentLength = postData.Length;

                using (var stream = webRequest.GetRequestStream())
                    stream.Write(postData, 0, postData.Length);
            }

            //get response
            var httpResponse = (HttpWebResponse)webRequest.GetResponse();
            var responseMessage = string.Empty;
            using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
                responseMessage = streamReader.ReadToEnd();

            return JsonConvert.DeserializeObject<TResponse>(responseMessage);
        }

        /// <summary>
        /// Handle request action
        /// </summary>
        /// <typeparam name="T">Response type</typeparam>
        /// <param name="requestAction">Request action</param>
        /// <returns>Response</returns>
        private T HandleRequestAction<T>(Func<T> requestAction)
        {
            try
            {
                //ensure that plugin is configured
                if (string.IsNullOrEmpty(_qualpaySettings.MerchantId) || !long.TryParse(_qualpaySettings.MerchantId, out long merchantId))
                    throw new NopException("Plugin not configured.");

                //process request action
                return requestAction();

            }
            catch (Exception exception)
            {
                var errorMessage = $"Qualpay error: {exception.Message}.";
                try
                {
                    //try to get error response
                    if (exception is WebException webException)
                    {
                        var httpResponse = (HttpWebResponse)webException.Response;
                        using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
                        {
                            var errorResponse = streamReader.ReadToEnd();
                            errorMessage = $"{errorMessage} Details: {errorResponse}";
                            return JsonConvert.DeserializeObject<T>(errorResponse);
                        }
                    }
                }
                finally
                {
                    //log errors
                    _logger.Error(errorMessage, exception, _workContext.CurrentCustomer);
                }

                return default(T);
            }
        }

        #endregion

        #region Methods

        #region Platform

        /// <summary>
        /// Get a customer from Qualpay Customer Vault by the passed identifier
        /// </summary>
        /// <param name="customerId">Customer identifier</param>
        /// <returns>Vault Customer</returns>
        public VaultCustomer GetCustomerById(string customerId)
        {
            var getCustomerRequest = new GetCustomerRequest { CustomerId = customerId };
            return ProcessPlatformRequest<GetCustomerRequest, CustomerVaultResponse>(getCustomerRequest)?.VaultCustomer;
        }

        /// <summary>
        /// Create new customer in Qualpay Customer Vault
        /// </summary>
        /// <param name="createCustomerRequest">Request parameters to create customer</param>
        /// <returns>Vault Customer</returns>
        public VaultCustomer CreateCustomer(CreateCustomerRequest createCustomerRequest)
        {
            return ProcessPlatformRequest<CreateCustomerRequest, CustomerVaultResponse>(createCustomerRequest)?.VaultCustomer;
        }

        /// <summary>
        /// Get customer billing cards from Qualpay Customer Vault
        /// </summary>
        /// <param name="customerId">Customer identifier</param>
        /// <returns>List of customer billing cards</returns>
        public IEnumerable<BillingCard> GetCustomerCards(string customerId)
        {
            var getCustomerCardsRequest = new GetCustomerCardsRequest { CustomerId = customerId };
            var response = ProcessPlatformRequest<GetCustomerCardsRequest, CustomerVaultResponse>(getCustomerCardsRequest);
            return response?.VaultCustomer?.BillingCards;
        }

        /// <summary>
        /// Create customer billing card in Qualpay Customer Vault
        /// </summary>
        /// <param name="createCustomerCardRequest">Request parameters to create card</param>
        /// <returns>True if customer card successfully created in the Vault; otherwise false</returns>
        public bool CreateCustomerCard(CreateCustomerCardRequest createCustomerCardRequest)
        {
            var response = ProcessPlatformRequest<CreateCustomerCardRequest, CustomerVaultResponse>(createCustomerCardRequest);
            return response?.ResponseCode == PlatformResponseCode.Success;
        }

        /// <summary>
        /// Delete customer billing card from Qualpay Customer Vault
        /// </summary>
        /// <param name="customerId">Customer identifier</param>
        /// <param name="cardId">Card identifier</param>
        /// <returns>True if customer card successfully deleted from the Vault; otherwise false</returns>
        public bool DeleteCustomerCard(string customerId, string cardId)
        {
            var deleteCustomerCardRequest = new DeleteCustomerCardRequest { CustomerId = customerId, CardId = cardId };
            var response = ProcessPlatformRequest<DeleteCustomerCardRequest, CustomerVaultResponse>(deleteCustomerCardRequest);
            return response?.ResponseCode == PlatformResponseCode.Success;
        }

        /// <summary>
        /// Get a webhook by the stored identifier
        /// </summary>
        /// <returns>Webhook</returns>
        public Webhook GetWebhook()
        {
            var getWebhookRequest = new GetWebhookRequest
            {
                WebhookId = long.TryParse(_qualpaySettings.WebhookId, out long webhookId) ? (long?)webhookId : null
            };
            return ProcessPlatformRequest<GetWebhookRequest, WebhookResponse>(getWebhookRequest)?.Webhook;
        }

        /// <summary>
        /// Create webhook
        /// </summary>
        /// <param name="createWebhookRequest">Request parameters to create webhook</param>
        /// <returns>Webhook</returns>
        public Webhook CreateWebhook(CreateWebhookRequest createWebhookRequest)
        {
            createWebhookRequest.WebhookNode = _qualpaySettings.MerchantId;
            createWebhookRequest.Secret = _qualpaySettings.SecurityKey;
            return ProcessPlatformRequest<CreateWebhookRequest, WebhookResponse>(createWebhookRequest)?.Webhook;
        }

        /// <summary>
        /// Validate received webhook that a request is initiated by Qualpay
        /// </summary>
        /// <param name="request">Request</param>
        /// <returns>True if webhook successfully validated; otherwise false</returns>
        public bool ValidateWebhook(HttpRequest request)
        {
            return HandleRequestAction(() =>
            {
                //try to get request message
                var message = string.Empty;
                using (var streamReader = new StreamReader(request.Body))
                    message = streamReader.ReadToEnd();

                if (string.IsNullOrEmpty(message))
                    throw new NopException("Webhook request is empty.");

                //ensure that request is signed using a signature header
                if (!request.Headers.TryGetValue(QualpayDefaults.WebhookSignatureHeaderName, out StringValues signatures))
                    throw new NopException("Webhook request not signed by a signature header.");

                //get encrypted string from the request message
                var keyBytes = Encoding.UTF8.GetBytes(_qualpaySettings.SecurityKey);
                var messageBytes = Encoding.UTF8.GetBytes(message);
                var encryptedBytes = new HMACSHA256(keyBytes).ComputeHash(messageBytes);
                var encryptedString = Convert.ToBase64String(encryptedBytes);

                //equal this encrypted string with received signatures
                if (!signatures.Any(signature => signature.Equals(encryptedString)))
                    throw new NopException("Webhook request isn't valid.");

                return true;
            });          
        }

        /// <summary>
        /// Create subscription
        /// </summary>
        /// <param name="createSubscriptionRequest">Request parameters to create subscription</param>
        /// <returns>Subscription</returns>
        public Subscription CreateSubscription(CreateSubscriptionRequest createSubscriptionRequest)
        {
            if (long.TryParse(_qualpaySettings.MerchantId, out long merchantId))
                createSubscriptionRequest.MerchantId = merchantId;

            return ProcessPlatformRequest<CreateSubscriptionRequest, SubscriptionResponse>(createSubscriptionRequest)?.Subscription;
        }

        /// <summary>
        /// Cancel subscription
        /// </summary>
        /// <param name="customerId">Customer identifier</param>
        /// <param name="subscriptionId">Subscription identifier</param>
        /// <returns>Subscription</returns>
        public Subscription CancelSubscription(string customerId, string subscriptionId)
        {
            var cancelSubscriptionRequest = new CancelSubscriptionRequest { CustomerId = customerId };
            if (long.TryParse(subscriptionId, out long subscriptionIdInt))
                cancelSubscriptionRequest.SubscriptionId = subscriptionIdInt;
            if (long.TryParse(_qualpaySettings.MerchantId, out long merchantId))
                cancelSubscriptionRequest.MerchantId = merchantId;

            return ProcessPlatformRequest<CancelSubscriptionRequest, SubscriptionResponse>(cancelSubscriptionRequest)?.Subscription;
        }

        #endregion

        #region Payment Gateway

        /// <summary>
        /// Authorize a transaction
        /// </summary>
        /// <param name="transactionRequest">Request parameters to authorize transaction</param>
        /// <returns>Response</returns>
        public TransactionResponse Authorize(TransactionRequest transactionRequest)
        {
            transactionRequest.TransactionType = TransactionType.Authorization;
            return ProcessPaymentGatewayRequest<TransactionRequest, TransactionResponse>(transactionRequest);
        }

        /// <summary>
        /// Sale
        /// </summary>
        /// <param name="transactionRequest">Request parameters to sale transaction</param>
        /// <returns>Response</returns>
        public TransactionResponse Sale(TransactionRequest transactionRequest)
        {
            transactionRequest.TransactionType = TransactionType.Sale;
            return ProcessPaymentGatewayRequest<TransactionRequest, TransactionResponse>(transactionRequest);
        }

        /// <summary>
        /// Capture an authorized transaction
        /// </summary>
        /// <param name="captureRequest">Request parameters to capture transaction</param>
        /// <returns>Response</returns>
        public CaptureResponse CaptureTransaction(CaptureRequest captureRequest)
        {
            return ProcessPaymentGatewayRequest<CaptureRequest, CaptureResponse>(captureRequest);
        }

        /// <summary>
        /// Void an authorized transaction
        /// </summary>
        /// <param name="voidRequest">Request parameters to void transaction</param>
        /// <returns>Response</returns>
        public VoidResponse VoidTransaction(VoidRequest voidRequest)
        {
            return ProcessPaymentGatewayRequest<VoidRequest, VoidResponse>(voidRequest);
        }

        /// <summary>
        /// Refund a charged transaction
        /// </summary>
        /// <param name="refundRequest">Request parameters to refund transaction</param>
        /// <returns>Response</returns>
        public RefundResponse Refund(RefundRequest refundRequest)
        {
            return ProcessPaymentGatewayRequest<RefundRequest, RefundResponse>(refundRequest);
        }

        #endregion

        #endregion
    }
}