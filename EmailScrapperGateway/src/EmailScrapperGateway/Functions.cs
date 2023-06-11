using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Runtime.Internal.Endpoints.StandardLibrary;
using Amazon.SQS;
using Amazon.SQS.Model;
using HtmlAgilityPack;
using Newtonsoft.Json;
using System.Net;
using System.Text.RegularExpressions;
using static EmailScrapperGateway.Functions;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace EmailScrapperGateway {
    public class Functions {
        const string baseSqsUrl = "https://sqs.eu-north-1.amazonaws.com/906774135415/";
        const string DomainsToProcessQURL = baseSqsUrl + "DomainsToProcessQ";
        const string URIsToProcessQURL = baseSqsUrl + "URIsToProcessQ";
        const string DomainEmailsTable = "DomainEmails";
        private const string emailPattern = @"(?:[a-z0-9!#$%&'*+\/=?^_`{|}~-]+(?:\.[a-z0-9!#$%&'*+\/=?^_`{|}~-]+)*|""""(?:[\x01-\x08\x0b\x0c\x0e-\x1f\x21\x23-\x5b\x5d-\x7f]|\\[\x01-\x09\x0b\x0c\x0e-\x7f])*"")[\s[]*@[\s\]]*(?:(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])?|\[(?:(?:(2(5[0-5]|[0-4][0-9])|1[0-9][0-9]|[1-9]?[0-9]))\.){3}(?:(2(5[0-5]|[0-4][0-9])|1[0-9][0-9]|[1-9]?[0-9])|[a-z0-9-]*[a-z0-9]:(?:[\x01-\x08\x0b\x0c\x0e-\x1f\x21-\x5a\x53-\x7f]|\\[\x01-\x09\x0b\x0c\x0e-\x7f])+)\])";
        private static readonly Regex emailRegex = new(emailPattern);
        /// <summary>
        /// Default constructor that Lambda will invoke.
        /// </summary>
        public Functions() {
        }


        /// <summary>
        /// A Lambda function to respond to HTTP Get methods from API Gateway
        /// </summary>
        /// <param name="request"></param>
        /// <returns>The API Gateway response.</returns>
        public APIGatewayProxyResponse GetFromCache(APIGatewayProxyRequest request, ILambdaContext context) {
            var body = JsonConvert.DeserializeObject<URIRequestBody>(request.Body);
            return GetProxyResponse(JsonConvert.SerializeObject(ReadFromCache(body.URIs)));
        }

        private CacheResponse ReadFromCache(string[] URIs) {
            List<UriInfo> uriInfos = new List<UriInfo>();
            using (AmazonDynamoDBClient dbClient = new AmazonDynamoDBClient()) {
                for (int i = 0; i <= (URIs.Length - 1)/ 10; i++) {
                    KeysAndAttributes keysAndAttributes = new() {
                        AttributesToGet = new List<string>() { "Domain", "Emails" },
                        Keys = URIs.Skip(i*10).Take(10).Select(uri => new Dictionary<string, AttributeValue>() { { "Domain", new AttributeValue(uri) } }).ToList()
                    };
                    var req = new BatchGetItemRequest(new Dictionary<string, KeysAndAttributes>() { { DomainEmailsTable, keysAndAttributes } });

                    uriInfos.AddRange(dbClient.BatchGetItemAsync(req).GetAwaiter().GetResult()
                         .Responses[DomainEmailsTable]
                         .Select(attributeToValueMap => new UriInfo() {
                             url = attributeToValueMap["Domain"].S,
                             emails = attributeToValueMap["Emails"].S.Split(',')
                         }).ToArray());
                }
            }
            return new CacheResponse() { data = uriInfos.ToArray() };
        }

        public class CacheResponse {
            public UriInfo[] data;
        }
        public class UriInfo {
            public string url;
            public string[] emails;
        }

        /// <summary>
        /// A Lambda function to respond to HTTP Get methods from API Gateway
        /// </summary>
        /// <param name="request"></param>
        /// <returns>The API Gateway response.</returns>
        public APIGatewayProxyResponse AddToQueue(APIGatewayProxyRequest request, ILambdaContext context) {
            var body = JsonConvert.DeserializeObject<URIRequestBody>(request.Body);
            string[] correctUris = body.URIs
                    .Select(uri => GetUri(uri).domain)
                    .Where(domain => domain != null)
                    .ToArray()!;
            UriInfo[] cachedData = ReadFromCache(correctUris).data;
            var response = new AddToQueueResponseBody() { DataFromCache = cachedData };
            List<SendMessageBatchRequestEntry> messages = correctUris.Except(cachedData.Select(uriInfo => uriInfo.url))
                    .Select(domain => new SendMessageBatchRequestEntry(Guid.NewGuid().ToString(), domain))
                    .ToList();
            if (!messages.Any()) { return GetProxyResponse(JsonConvert.SerializeObject(response)); }
            HashSet<string> successfulIds = QueueMessages(DomainsToProcessQURL, messages);
            if (!successfulIds.Any()) { return GetProxyResponse(JsonConvert.SerializeObject(response)); }
            string[] queuedDomains = messages
                .Where(requestedMessage => successfulIds.Contains(requestedMessage.Id))
                .Select(requestedMessage => requestedMessage.MessageBody)
                .ToArray();
            response.QueuedDomains = queuedDomains;
            return GetProxyResponse(JsonConvert.SerializeObject(response));
        }

        private static HashSet<string> QueueMessages(string url, List<SendMessageBatchRequestEntry> messages) {
            using (var sqsClient = new AmazonSQSClient()) {
                HashSet<string> successfulIds = new HashSet<string>();
                for (int i = 0; i <= (messages.Count - 1) / 10; i++) {
                    SendMessageBatchResponse sqsResponse = sqsClient
                        .SendMessageBatchAsync(url, messages.Skip(i * 10).Take(10).ToList())
                        .GetAwaiter().GetResult();
                    successfulIds.UnionWith(sqsResponse.Successful.Select(successfulResponse => successfulResponse.Id));
                }
                return successfulIds;
            } 
        }

        private static APIGatewayProxyResponse GetProxyResponse(string body) {
            return new APIGatewayProxyResponse {
                StatusCode = (int)HttpStatusCode.OK,
                Body = body,
                Headers = new Dictionary<string, string> {
                        { "Content-Type", "application/json" },
                        { "Access-Control-Allow-Origin", "*" },
                        { "Access-Control-Allow-Methods", "*" },
                        { "Access-Control-Allow-Headers", "*" },
                        { "Access-Control-Expose-Headers", "*" },
                    }
            };
        }

        public class URIRequestBody {
            public string[] URIs;
        }
        public class AddToQueueResponseBody {
            public string[] QueuedDomains;
            public UriInfo[] DataFromCache;
        }

        public async Task ProcessDomain(SQSEvent evnt, ILambdaContext context) {
            foreach (SQSEvent.SQSMessage? message in evnt.Records) {
                await ProcessDomainMessageAsync(message, context);
            }
        }

        public async Task ProcessURI(SQSEvent evnt, ILambdaContext context) {
            foreach (SQSEvent.SQSMessage? message in evnt.Records) {
                await ProcessURIMessageAsync(message, context);
            }
        }

        private async Task ProcessURIMessageAsync(SQSEvent.SQSMessage message, ILambdaContext context) {
            (string absoluteUri, string domain)= GetUri(message.Body);
            using (HttpClient client = new HttpClient()) {
                using (HttpResponseMessage response = client.GetAsync(absoluteUri).Result) {
                    using (HttpContent content = response.Content) {
                        string html = await content.ReadAsStringAsync();
                        html = html.Replace("[at]", "@");
                        html = html.Replace("(at)", "@");
                        html = html.Replace(" (at) ", "@");
                        html = html.Replace(" [at] ", "@");
                        html = html.Replace("[dot]", ".");
                        html = html.Replace(" [dot] ", ".");
                        string emails = string.Join(",",
                            emailRegex.Matches(html)
                            .Select(match => RemoveWhitespace(match.Value))
                            .Where(email => emailRegex.IsMatch(email) && !email.Contains('/'))
                            .Distinct()
                        );
                        var request = new PutItemRequest {
                            TableName = DomainEmailsTable,
                            Item = new Dictionary<string, AttributeValue>()
                                  {
                                      { "Domain", new AttributeValue(domain.ToLower()) },
                                      { "Emails", new AttributeValue(emails)},
                                  }
                        };
                        using (AmazonDynamoDBClient dbClient = new AmazonDynamoDBClient()) {
                            var r =
                            await dbClient.PutItemAsync(request);
                        }
                    }
                }
            }
        }
        private static string RemoveWhitespace(string input) {
            return new string(input.ToCharArray()
                .Where(c => !Char.IsWhiteSpace(c))
                .ToArray());
        }

        private async Task ProcessDomainMessageAsync(SQSEvent.SQSMessage message, ILambdaContext context) {
            (string absoluteUri, string domain) = GetUri(message.Body);
            using (HttpClient client = new HttpClient()) {
                using (HttpResponseMessage response = client.GetAsync(absoluteUri).Result) {
                    using (HttpContent content = response.Content) {
                        string html = await content.ReadAsStringAsync();
                        var doc = new HtmlDocument();
                        doc.LoadHtml(html);
                        var hrefList = doc.DocumentNode.SelectNodes("//a")
                          .Select(aNode => aNode.GetAttributeValue("href", ""))
                          .Where(hrefVal => !string.IsNullOrEmpty(hrefVal))
                          .Where(linktext => linktext.ToLower().Contains("contact") || linktext.ToLower().Contains("write")
                                          || linktext.ToLower().Contains("about") || linktext.ToLower().Contains("advertise")
                                          || linktext.ToLower().Contains("with") || linktext.ToLower().Contains("touch"))
                          .Distinct()
                          .Take(50)
                          .ToList();
                        var urisToQueue = new List<string> {
                            absoluteUri
                        };
                        foreach (var href in hrefList) {
                            string internalAbsoluteLinkText = href;
                            if (!href.Contains(domain)) {
                                if (href.StartsWith("http") || href.StartsWith("#")) { continue; }
                                internalAbsoluteLinkText = absoluteUri + href;
                            }
                            if (GetUri(internalAbsoluteLinkText).domain?.ToLower() != domain.ToLower()) { continue; }
                            urisToQueue.Add(internalAbsoluteLinkText);
                        }
                        List<SendMessageBatchRequestEntry> messages = urisToQueue
                            .Select(uri => new SendMessageBatchRequestEntry(Guid.NewGuid().ToString(), uri))
                            .ToList();
                        QueueMessages(URIsToProcessQURL, messages);
                    }
                }
            }
        }

        private static APIGatewayProxyResponse EmptyResponse = GetProxyResponse("{}");
        private static (string? absoluteUri, string? domain) GetUri(string initialUri) {
            Uri uri;
            try {
                uri = new UriBuilder(initialUri.Trim()).Uri;
            } catch { 
                return (null,null);
            }
            string absoluteUri = uri.AbsoluteUri;
            if (absoluteUri[..7] == @"http://") {
                absoluteUri = "https://" + absoluteUri[7..];
            }
            string domain = uri.Host;
            if (domain.StartsWith("www.")) {
                domain = domain[4..];
            }
            return (absoluteUri.ToLower(), domain.ToLower());
        }

        /// <summary>
        /// A Lambda function to respond to HTTP Get methods from API Gateway
        /// </summary>
        /// <param name="request"></param>
        /// <returns>The API Gateway response.</returns>
        public APIGatewayProxyResponse Option(APIGatewayProxyRequest request, ILambdaContext context) => EmptyResponse;
    }
}