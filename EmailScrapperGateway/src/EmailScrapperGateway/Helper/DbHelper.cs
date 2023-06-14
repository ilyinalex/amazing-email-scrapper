using Amazon.DynamoDBv2.Model;
using Amazon.DynamoDBv2;
using EmailScrapperGateway.DTO;

namespace EmailScrapperGateway.Helper
{
    internal static class DbHelper
    {
        const string DomainEmailsTable = "DomainEmails";
        private const int BatchSize = 10;
        public static async Task SaveDomainWithEmailsAsync(string domain, string[] emails)
        {
            var request = new PutItemRequest
            {
                TableName = DomainEmailsTable,
                Item = new Dictionary<string, AttributeValue>() {
                     { "Domain", new AttributeValue(domain.ToLower()) },
                     { "Emails", new AttributeValue(string.Join(",", emails))},
                }
            };
            using (AmazonDynamoDBClient dbClient = new AmazonDynamoDBClient())
            {
                PutItemResponse r = await dbClient.PutItemAsync(request);
            }
        }

        public static async Task<CacheResponse> ReadFromCacheAsync(string[] URIs)
        {
            List<UriInfo> uriInfos = new();
            using (AmazonDynamoDBClient dbClient = new AmazonDynamoDBClient())
            {
                for (int i = 0; i <= (URIs.Length - 1) / BatchSize; i++)
                {
                    KeysAndAttributes keysAndAttributes = new()
                    {
                        AttributesToGet = new List<string>() { "Domain", "Emails" },
                        Keys = URIs.Skip(i * BatchSize).Take(BatchSize)
                            .Select(uri => new Dictionary<string, AttributeValue>() { { "Domain", new AttributeValue(uri) } }).ToList()
                    };
                    var req = new BatchGetItemRequest(new Dictionary<string, KeysAndAttributes>() { { DomainEmailsTable, keysAndAttributes } });
                    BatchGetItemResponse resp = await dbClient.BatchGetItemAsync(req);
                    uriInfos.AddRange(resp
                         .Responses[DomainEmailsTable]
                         .Select(attributeToValueMap => new UriInfo()
                         {
                             url = attributeToValueMap["Domain"].S,
                             emails = attributeToValueMap["Emails"].S.Split(',')
                         }).ToArray());
                }
            }
            return new CacheResponse() { data = uriInfos.ToArray() };
        }
    }
}
