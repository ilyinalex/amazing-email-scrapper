using Amazon.SQS.Model;
using Amazon.SQS;

namespace EmailScrapperGateway.Helper
{
    internal static class QueueHelper
    {
        private const int BatchSize = 10;
        public async static Task<HashSet<string>> QueueMessagesAsync(string queueUrl, IEnumerable<string> bodies)
        {
            using var sqsClient = new AmazonSQSClient();
            SendMessageBatchRequestEntry[] messages = bodies.Select(domain => new SendMessageBatchRequestEntry(Guid.NewGuid().ToString(), domain)).ToArray();
            HashSet<string> successfulIds = new();
            for (int i = 0; i <= (messages.Length - 1) / BatchSize; i++)
            {
                SendMessageBatchResponse sqsResponse = await sqsClient.SendMessageBatchAsync(queueUrl, messages.Skip(i * BatchSize).Take(BatchSize).ToList());
                successfulIds.UnionWith(sqsResponse.Successful.Select(successfulResponse => successfulResponse.Id));
            }
            return messages.Where(message => successfulIds.Contains(message.Id)).Select(message => message.MessageBody).ToHashSet();
        }
    }
}
