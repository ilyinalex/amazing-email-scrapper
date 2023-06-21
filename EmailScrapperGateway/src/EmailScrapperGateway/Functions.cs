using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Newtonsoft.Json;
using System.Net;
using static EmailScrapperGateway.Helper.URIHelper;
using static EmailScrapperGateway.Helper.HtmlContentHelper;
using static EmailScrapperGateway.Helper.DbHelper;
using static EmailScrapperGateway.Helper.QueueHelper;
using EmailScrapperGateway.DTO;
using Amazon.DynamoDBv2;
using System.Net.Sockets;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace EmailScrapperGateway
{
    public class Functions {
        private const string baseSqsUrl = "https://sqs.eu-north-1.amazonaws.com/906774135415/";
        private const string DomainsToProcessQURL = baseSqsUrl + "DomainsToProcessQ";
        private const string URIsToProcessQURL = baseSqsUrl + "URIsToProcessQ";
        private const int MaximumURIcountToSearch = 20;

       
        private readonly static APIGatewayProxyResponse EmptyResponse = GetProxyResponse("");
        private readonly static APIGatewayProxyResponse NotAuthorizedResponse = new() { 
            StatusCode = 401,
            Headers = GetDefaultHeaders()
        };

        public APIGatewayProxyResponse GetFromCache(APIGatewayProxyRequest request, ILambdaContext context) {
            using AmazonDynamoDBClient dbClient = new AmazonDynamoDBClient();
            URIRequestBody? body = GetUriRequestBodyIfAuthorized(request, dbClient).GetAwaiter().GetResult();
            if (body == null) { return NotAuthorizedResponse; }
            CacheResponse domainsFromCache = ReadUriInfoFromCacheAsync(body.URIs).GetAwaiter().GetResult();
            body.UserInfo.FoundDomains.AddRange(domainsFromCache.data.Select(data => data.url));
            PutUserInfo(body.UserInfo, dbClient).GetAwaiter().GetResult();
            return GetProxyResponse(domainsFromCache);
        }
        public APIGatewayProxyResponse GetQueueInfo(APIGatewayProxyRequest request, ILambdaContext context) {
            using AmazonDynamoDBClient dbClient = new AmazonDynamoDBClient();
            QueueInfoRequestBody? body = GetQueueInfoRequestBodyIfAuthorized(request, dbClient).GetAwaiter().GetResult();
            if (body == null) { return NotAuthorizedResponse; }
            return GetProxyResponse(new QueueInfo() { 
                DomainQueueMessageNumber = GetNumberOfMessages(DomainsToProcessQURL).GetAwaiter().GetResult(),
                UriQueueMessageNumber = GetNumberOfMessages(URIsToProcessQURL).GetAwaiter().GetResult()
            });
        }

        public APIGatewayProxyResponse AddToQueue(APIGatewayProxyRequest request, ILambdaContext context) {
            using AmazonDynamoDBClient dbClient = new AmazonDynamoDBClient();
            URIRequestBody? body = GetUriRequestBodyIfAuthorized(request, dbClient).GetAwaiter().GetResult();
            if (body == null) { return NotAuthorizedResponse; }
            string[] correctUris = body.URIs
                    .Select(uri => GetUri(uri).domain)
                    .Where(domain => domain != null)
                    .Distinct()
                    .ToArray()!;
            UriInfo[] cachedData = ReadUriInfoFromCacheAsync(correctUris).GetAwaiter().GetResult().data;
            var response = new AddToQueueResponseBody() { DataFromCache = cachedData };
            string[] domainsToQueue = correctUris.Except(cachedData.Select(uriInfo => uriInfo.url)).ToArray();
            if (!domainsToQueue.Any()) { return GetProxyResponse(response); }

            if (body.UserInfo.RemainingDomainRequestCount < domainsToQueue.Length) { 
                return GetNotEnoughPaidDomainsResponse(domainsToQueue.Length, body.UserInfo.RemainingDomainRequestCount); 
            }
            body.UserInfo.RequestedDomains.AddRange(domainsToQueue);
            PutUserInfo(body.UserInfo, dbClient).GetAwaiter().GetResult();

            response.QueuedDomains = QueueMessagesAsync(DomainsToProcessQURL, domainsToQueue).GetAwaiter().GetResult().ToArray();
            return GetProxyResponse(response);
        }

        public async Task ProcessDomain(SQSEvent evnt, ILambdaContext context) {
            foreach (SQSEvent.SQSMessage? message in evnt.Records) {
                try {
                    var urisToQueue = await GetContactLinksAsync(message.Body, context.Logger);
                    if (!urisToQueue.Any()) { continue; }
                    await QueueMessagesAsync(URIsToProcessQURL, urisToQueue.Take(MaximumURIcountToSearch));
                } catch (Exception e) { context.Logger.LogError(e.Message); }
            }
        }

        public async Task ProcessURI(SQSEvent evnt, ILambdaContext context) {
            foreach (SQSEvent.SQSMessage? message in evnt.Records) {
                try {
                    (string? absoluteUri, string? domain) = GetUri(message.Body);
                    if (absoluteUri == null || domain == null) { return; }
                    (string[] emails, bool isContactForm) = await GetEmailsAsync(absoluteUri, context.Logger);
                    if (!isContactForm && !emails.Any()) { continue; }
                    if (isContactForm) {
                        await SaveDomainWithEmailsAsync(domain, emails, absoluteUri, context.Logger);
                        continue;
                    }
                    await SaveDomainWithEmailsAsync(domain, emails, context.Logger);
                } catch (Exception e) { context.Logger.LogError(e.Message); }
            }
        }

        public APIGatewayProxyResponse Option(APIGatewayProxyRequest request, ILambdaContext context) => EmptyResponse;

        private async static Task<URIRequestBody?> GetUriRequestBodyIfAuthorized(APIGatewayProxyRequest request, AmazonDynamoDBClient dbClient) {
            URIRequestBody? body = JsonConvert.DeserializeObject<URIRequestBody>(request.Body);
            if (body?.User == null) { return null; }
            UserInfo? userInfo = await GetUserInfo(body.User, dbClient);
            if (userInfo == null) { return null; }
            body.UserInfo = userInfo;
            return body;
        }

        private async static Task<QueueInfoRequestBody?> GetQueueInfoRequestBodyIfAuthorized(APIGatewayProxyRequest request, AmazonDynamoDBClient dbClient) {
            QueueInfoRequestBody? body = JsonConvert.DeserializeObject<QueueInfoRequestBody>(request.Body);
            if (body?.User == null) { return null; };
            if (null == await GetUserInfo(body.User, dbClient)) { return null; }
            return body;
        }

        private static APIGatewayProxyResponse GetProxyResponse(object? body) => new() {
            StatusCode = (int)HttpStatusCode.OK,
            Body = JsonConvert.SerializeObject(body),
            Headers = GetDefaultHeaders()
        };
        
        private static APIGatewayProxyResponse GetNotEnoughPaidDomainsResponse(int requestedDomainsCount, int allowedDomainRequestCount) => new() {
            StatusCode = 402,
            Body = JsonConvert.SerializeObject(new { requestedDomainsCount, allowedDomainRequestCount }),
            Headers = GetDefaultHeaders()
        };
        private static Dictionary<string, string> GetDefaultHeaders() => new() {
            { "Content-Type", "application/json" },
            { "Access-Control-Allow-Origin", "*" },
            { "Access-Control-Allow-Methods", "*" },
            { "Access-Control-Allow-Headers", "*" },
            { "Access-Control-Expose-Headers", "*" },
        };
    }
}