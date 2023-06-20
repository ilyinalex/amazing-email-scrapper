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

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace EmailScrapperGateway
{
    public class Functions {
        private const string AuthorizedUser = "Adm";
        private const string baseSqsUrl = "https://sqs.eu-north-1.amazonaws.com/906774135415/";
        private const string DomainsToProcessQURL = baseSqsUrl + "DomainsToProcessQ";
        private const string URIsToProcessQURL = baseSqsUrl + "URIsToProcessQ";
        private const int MaximumURIcountToSearch = 20; 
        
        private readonly static APIGatewayProxyResponse EmptyResponse = GetProxyResponse("");
        private readonly static APIGatewayProxyResponse NotAuthorizedResponse = new() { 
            StatusCode = 401,
            Headers = new Dictionary<string, string> {
                        { "Content-Type", "application/json" },
                        { "Access-Control-Allow-Origin", "*" },
                        { "Access-Control-Allow-Methods", "*" },
                        { "Access-Control-Allow-Headers", "*" },
                        { "Access-Control-Expose-Headers", "*" },
                }
        };

        public APIGatewayProxyResponse GetFromCache(APIGatewayProxyRequest request, ILambdaContext context) {
            URIRequestBody? body = GetUriRequestBodyIfAuthorized(request);
            if (body == null) { return NotAuthorizedResponse; }
            return GetProxyResponse(ReadFromCacheAsync(body.URIs).GetAwaiter().GetResult());
        }
        public APIGatewayProxyResponse GetQueueInfo(APIGatewayProxyRequest request, ILambdaContext context) {
            QueueInfoRequestBody? body = GetQueueInfoRequestBodyIfAuthorized(request);
            if (body == null) { return NotAuthorizedResponse; }
            return GetProxyResponse(new QueueInfo() { 
                DomainQueueMessageNumber = GetNumberOfMessages(DomainsToProcessQURL).GetAwaiter().GetResult(),
                UriQueueMessageNumber = GetNumberOfMessages(URIsToProcessQURL).GetAwaiter().GetResult()
            });
        }

        public APIGatewayProxyResponse AddToQueue(APIGatewayProxyRequest request, ILambdaContext context) {
            URIRequestBody? body = GetUriRequestBodyIfAuthorized(request);
            if (body == null) { return NotAuthorizedResponse; }
            string[] correctUris = body.URIs
                    .Select(uri => GetUri(uri).domain)
                    .Where(domain => domain != null)
                    .ToArray()!;
            UriInfo[] cachedData = ReadFromCacheAsync(correctUris).GetAwaiter().GetResult().data;
            var response = new AddToQueueResponseBody() { DataFromCache = cachedData };
            IEnumerable<string> domainsToQueue = correctUris.Except(cachedData.Select(uriInfo => uriInfo.url));
            if (!domainsToQueue.Any()) { return GetProxyResponse(response); }
            response.QueuedDomains = QueueMessagesAsync(DomainsToProcessQURL, domainsToQueue).GetAwaiter().GetResult().ToArray();
            return GetProxyResponse(response);
        }

        public async Task ProcessDomain(SQSEvent evnt, ILambdaContext context) {
            foreach (SQSEvent.SQSMessage? message in evnt.Records) {
                try {
                    var urisToQueue = await GetContactLinksAsync(message.Body);
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
                    (string[] emails, bool isContactForm) = await GetEmailsAsync(absoluteUri);
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

        private static URIRequestBody? GetUriRequestBodyIfAuthorized(APIGatewayProxyRequest request) {
            URIRequestBody? body = JsonConvert.DeserializeObject<URIRequestBody>(request.Body);
            if (body?.User != AuthorizedUser) { return null; };
            return body;
        }

        private static QueueInfoRequestBody? GetQueueInfoRequestBodyIfAuthorized(APIGatewayProxyRequest request) {
            QueueInfoRequestBody? body = JsonConvert.DeserializeObject<QueueInfoRequestBody>(request.Body);
            if (body?.User != AuthorizedUser) { return null; };
            return body;
        }

        private static APIGatewayProxyResponse GetProxyResponse(object? body) {
            return new APIGatewayProxyResponse {
                StatusCode = (int)HttpStatusCode.OK,
                Body = JsonConvert.SerializeObject(body),
                Headers = new Dictionary<string, string> {
                        { "Content-Type", "application/json" },
                        { "Access-Control-Allow-Origin", "*" },
                        { "Access-Control-Allow-Methods", "*" },
                        { "Access-Control-Allow-Headers", "*" },
                        { "Access-Control-Expose-Headers", "*" },
                }
            };
        }
    }
}