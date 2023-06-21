using DnsClient;

namespace EmailScrapperGateway {
    internal static class DNSHelper {
        public async static Task<string[]> GetEmailsWithValidDomains(List<string> emails) {
            var lookup = new LookupClient();
            List<string> results = new();
            foreach (IGrouping<string, string> emailGroup in emails.Where(email => email.Contains('@')).GroupBy(email => email.Split('@')[1])) {
                var dnsResult = await lookup.QueryAsync(emailGroup.Key, QueryType.MX);
                if (dnsResult.Answers.Count == 0) { continue; }
                results.AddRange(emailGroup.ToArray());
            }
            return results.ToArray();
        }
    }
}
