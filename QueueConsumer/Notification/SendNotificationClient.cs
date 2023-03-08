﻿using NewRelic.Api.Agent;

using Newtonsoft.Json.Linq;
using QueueConsumer.Models;

using RestSharp;
using RestSharp.Authenticators;

using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace QueueConsumer.Notification;

public class SendNotificationClient
{
    private readonly IRestClient _restClient;
    private readonly QueueConsumerJwt _queueConsumerJwt;

    public SendNotificationClient(QueueConsumerConfiguration configuration, QueueConsumerJwt queueConsumerJwt)
    {
        _restClient = GetRestClient(configuration, queueConsumerJwt);
        _queueConsumerJwt = queueConsumerJwt;
    }

    [Trace]
    public async Task<bool> SendNotification(QueueConsumerConfiguration configuration, string urlFromMessage, string message)
    {
        var request = new RestRequest(ExtractUrl(configuration, urlFromMessage, message), Method.POST);
        request.AddParameter("application/json; charset=utf-8", message, ParameterType.RequestBody);

        SetAuthenticationMethod(configuration, _queueConsumerJwt);

        var response = await _restClient.ExecuteAsync(request);

        return configuration.StatusCodeAcceptToSuccessList.Contains((int)response.StatusCode);
    }

    private static IRestClient GetRestClient(QueueConsumerConfiguration configuration, QueueConsumerJwt queueConsumerJwt)
    {
        var config = new RestClientAutologConfiguration
        {
            EnabledLog = configuration.LogEnabled,
            RequestJsonBlacklist = configuration.LogBlacklistList.ToArray(),
            ResponseJsonBlacklist = configuration.LogBlacklistList.ToArray(),
        };

        var restClient = new RestClientAutolog(config)
        {
            Timeout = configuration.TimeoutInSeconds * 1000,
            AllowMultipleDefaultParametersWithSameName = false
        };

        if (!string.IsNullOrEmpty(configuration.UserAgent))
        {
            restClient.UserAgent = configuration.UserAgent;
        }

        return restClient;
    }

    private void SetAuthenticationMethod(QueueConsumerConfiguration configuration, QueueConsumerJwt queueConsumerJwt)
    {
        if (configuration.AuthenticationMethod == "AuthToken" && !_restClient.DefaultParameters.Any(x => x.Name == "Authorization"))
        {
            _restClient.AddDefaultHeader("Authorization", configuration.AuthToken);
        }
        else if (configuration.AuthenticationMethod == "Jwt")
        {
            queueConsumerJwt.HandleAccessToken();
            _restClient.Authenticator = new JwtAuthenticator(queueConsumerJwt.CurrentAccessToken.AccessToken);
        }
        else if (configuration.AuthenticationMethod == "Basic")
        {
            var user = configuration.User ?? "";
            var pass = configuration.Pass ?? "";
            _restClient.Authenticator = new HttpBasicAuthenticator(user, pass);
        }
    }

    private static string ExtractUrl(QueueConsumerConfiguration configuration, string urlFromMessage, string message)
    {
        var url = urlFromMessage ?? configuration.Url;

        if (!configuration.ShouldUseUrlWithDynamicMatch)
        {
            return url;
        }

        var messageAsJsonObj = JObject.Parse(message);

        var fieldsPathToMatch = Regex.Matches(configuration.Url, @"{{[\w\.]*}}");

        foreach (var fieldPathAsObj in fieldsPathToMatch)
        {
            string fieldPath = fieldPathAsObj.ToString().Trim('{', '}');
            var fieldValue = messageAsJsonObj.SelectToken(fieldPath);
            url = url.Replace("{{" + fieldPath + "}}", fieldValue.Value<string>());
        }

        return url;
    }
}

public static class RestClientExtensions
{
    public static async Task<IRestResponse> ExecuteTaskAsync(this RestClient restClient, RestRequest request)
    {
        if (restClient == null)
        {
            throw new NullReferenceException();
        }

        var taskCompletionSource = new TaskCompletionSource<IRestResponse>();

        var response = await restClient.ExecuteAsync(request);
        if (response.ErrorException != null)
        {
            taskCompletionSource.TrySetException(response.ErrorException);
        }
        else
        {
            taskCompletionSource.TrySetResult(response);
        };

        return await taskCompletionSource.Task;
    }
}