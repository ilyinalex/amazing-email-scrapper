using Amazon.DynamoDBv2.Model;
using Amazon.DynamoDBv2;
using EmailScrapperGateway.DTO;
using Amazon.Lambda.Core;
using System;
using System.Linq;

namespace EmailScrapperGateway.Helper {
    internal static class DbHelper {
        const string DomainEmailsTable = "DomainEmails";
        const string UserRequestsTable = "UserRequests";
        const string UserField = "User";
        const string RequestedDomainsField = "RequestedDomains";
        const string FoundDomainsField = "FoundDomains";
        const string AllowedDomainRequestCountField = "AllowedDomainRequestCount";
        const string DomainField = "Domain";
        const string EmailsField = "Emails";
        const string ContactFormUrlsField = "ContactFormUrls";
        private const int BatchSize = 10;
        private const int TryLimit = 5;
        public static async Task SaveDomainWithEmailsAsync(string domain, string[] emails, ILambdaLogger logger) => await SaveDomainWithEmailsAsync(domain, emails, "", logger);
        public static async Task SaveDomainWithEmailsAsync(string domain, string[] emails, string contactFormUrl, ILambdaLogger logger) {
            using AmazonDynamoDBClient dbClient = new();
            int tryCount = 0;
            while (tryCount < TryLimit) {
                try {
                    tryCount++;
                    CacheResponse cacheResponse = await ReadUriInfoFromCacheAsync(new[] { domain });
                    IEnumerable<string> oldEmails = cacheResponse.data.SelectMany(uriInfo => uriInfo.emails);
                    IEnumerable<string> oldContactFormUrls = cacheResponse.data.SelectMany(uriInfo => uriInfo.contactFormUrls);
                    List<string> emailsToPut = new();
                    emailsToPut.AddRange(emails);
                    emailsToPut.AddRange(oldEmails);
                    List<string> contactFormUrlsToPut = new();
                    if (contactFormUrl != "") {
                        contactFormUrlsToPut.Add(contactFormUrl);
                    }
                    contactFormUrlsToPut.AddRange(oldContactFormUrls);
                    var request = new PutItemRequest {
                        TableName = DomainEmailsTable,
                        Item = new Dictionary<string, AttributeValue>() {
                             { DomainField, new AttributeValue(domain.ToLower()) },
                             { EmailsField, new AttributeValue(string.Join(",", emailsToPut.Where(email => !string.IsNullOrEmpty(email)).Distinct())) },
                             { ContactFormUrlsField, new AttributeValue(string.Join(",", contactFormUrlsToPut.Where(email => !string.IsNullOrEmpty(email)).Distinct())) },
                        },
                        ExpressionAttributeNames = new Dictionary<string, string>() {
                              { "#Emails", EmailsField },
                              { "#ContactFormUrls", ContactFormUrlsField },
                        },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>() {
                            {":oldEmails",new AttributeValue { S = string.Join(",", oldEmails) } },
                            {":oldContactFormUrls",new AttributeValue { S = string.Join(",", oldContactFormUrls) } }
                        },
                        ConditionExpression = "#Emails = :oldEmails AND #ContactFormUrls = :oldContactFormUrls OR attribute_not_exists(#Emails)"
                    };
                    PutItemResponse r = await dbClient.PutItemAsync(request);
                    break;
                } catch (ConditionalCheckFailedException) {
                    logger.LogInformation($"Conditional check failed {tryCount} times");
                }
            }
        }

        public static async Task<CacheResponse> ReadUriInfoFromCacheAsync(string[] URIs) {
            List<UriInfo> uriInfos = new();
            using (AmazonDynamoDBClient dbClient = new()) {
                for (int i = 0; i <= (URIs.Length - 1) / BatchSize; i++) {
                    KeysAndAttributes keysAndAttributes = new() {
                        AttributesToGet = new List<string>() { DomainField, EmailsField, ContactFormUrlsField },
                        Keys = URIs
                            .Skip(i * BatchSize)
                            .Take(BatchSize)
                            .Select(uri => new Dictionary<string, AttributeValue>() { { DomainField, new AttributeValue(uri) } })
                            .ToList()
                    };
                    var req = new BatchGetItemRequest(new Dictionary<string, KeysAndAttributes>() { { DomainEmailsTable, keysAndAttributes } });
                    BatchGetItemResponse resp = await dbClient.BatchGetItemAsync(req);
                    uriInfos.AddRange(resp
                         .Responses[DomainEmailsTable]
                         .Select(attributeToValueMap => new UriInfo() {
                             url = attributeToValueMap[DomainField].S,
                             emails = attributeToValueMap[EmailsField].S.Split(','),
                             contactFormUrls = attributeToValueMap[ContactFormUrlsField].S.Split(',')
                         }).ToArray());
                }
            }
            return new CacheResponse() { data = uriInfos.ToArray() };
        }

        public static async Task PutUserInfo(UserInfo userInfo, AmazonDynamoDBClient dbClient) {
            var request = new PutItemRequest {
                TableName = UserRequestsTable,
                Item = new Dictionary<string, AttributeValue>() {
                     { UserField, new AttributeValue(userInfo.User) },
                     { RequestedDomainsField, new AttributeValue(userInfo.RequestedDomains) },
                     { FoundDomainsField, new AttributeValue(userInfo.FoundDomains) },
                     { AllowedDomainRequestCountField, new AttributeValue() { N = userInfo.AllowedDomainRequestCount.ToString() } },
                }
            };
            _ = await dbClient.PutItemAsync(request);
        }

        public static async Task<UserInfo?> GetUserInfo(string user, AmazonDynamoDBClient dbClient) {
            GetItemResponse resp = await dbClient.GetItemAsync(new GetItemRequest(UserRequestsTable,
                new Dictionary<string, AttributeValue>() { { UserField, new AttributeValue(user) } }));
            if (!resp.IsItemSet) { return null; }
            var userInfo = new UserInfo {
                User = resp.Item[UserField].S,
                RequestedDomains = resp.Item[RequestedDomainsField].SS,
                FoundDomains = resp.Item[FoundDomainsField].SS,
                AllowedDomainRequestCount = int.Parse(resp.Item[AllowedDomainRequestCountField].N)
            };
            userInfo.RemainingDomainRequestCount = userInfo.AllowedDomainRequestCount - userInfo.FoundDomains.Intersect(userInfo.RequestedDomains).Count() + 1;
            return userInfo;
        }
    }
}
