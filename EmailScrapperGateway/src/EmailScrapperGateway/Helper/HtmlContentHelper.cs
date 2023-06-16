using HtmlAgilityPack;
using System.Text.RegularExpressions;
using static EmailScrapperGateway.Helper.URIHelper;
using static EmailScrapperGateway.CloudFlareDecrypter;

namespace EmailScrapperGateway.Helper {
    internal static class HtmlContentHelper {
        private const string emailPattern = @"(?:[a-z0-9!#$%&'*+\/=?^_`{|}~-]+(?:\.[a-z0-9!#$%&'*+\/=?^_`{|}~-]+)*|""""(?:[\x01-\x08\x0b\x0c\x0e-\x1f\x21\x23-\x5b\x5d-\x7f]|\\[\x01-\x09\x0b\x0c\x0e-\x7f])*"")[\s[]*@[\s\]]*(?:(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])?|\[(?:(?:(2(5[0-5]|[0-4][0-9])|1[0-9][0-9]|[1-9]?[0-9]))\.){3}(?:(2(5[0-5]|[0-4][0-9])|1[0-9][0-9]|[1-9]?[0-9])|[a-z0-9-]*[a-z0-9]:(?:[\x01-\x08\x0b\x0c\x0e-\x1f\x21-\x5a\x53-\x7f]|\\[\x01-\x09\x0b\x0c\x0e-\x7f])+)\])";
        private const int HttpTimeout = 25000;
        private static readonly HashSet<string> ContactKeyWords = new() { "contact", "write", "about", "advertise", "with", "touch" };
        private static readonly Regex emailRegex = new(emailPattern);

        public static async Task<List<string>> GetContactLinksAsync(string url) {
            (string? absoluteUri, string? domain) = GetUri(url);
            if (absoluteUri == null || domain == null) { return new List<string>(); }
            var doc = new HtmlDocument();
            doc.LoadHtml(await GetHttpContentAsync(absoluteUri));
            HtmlNode[] linkNodes = doc.DocumentNode.SelectNodes("//a")?.ToArray() ?? Array.Empty<HtmlNode>();

            List<string> hrefList = new() { absoluteUri };
            hrefList.AddRange(linkNodes
                .Select(aNode => aNode.GetAttributeValue("href", ""))
                .Where(linktext => ContactKeyWords.Any(contactKeyWord => linktext.ToLower().Contains(contactKeyWord)))
                .Distinct()
                .OrderBy(linktext => linktext.Length));
            return GetInternalUris(hrefList, domain, absoluteUri).Distinct().ToList();
        }
        public static async Task<(string[], bool)> GetEmailsAsync(string absoluteUri) {
            string html = await GetHttpContentAsync(absoluteUri);
            html = ReplaceSimpleAntiScrapping(html);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            List<string> emails = new();
            emails.AddRange(GetTextsBySelector(doc, "//text()").FilterEmails());
            emails.AddRange(GetAttributeValues(doc, "href").FilterEmails());
            emails.AddRange(GetAttributeValues(doc, "data-cfemail").Select(encryptedText => Decrypt(encryptedText)).FilterEmails());

            return (emails.ToArray(), IsWordPresentAndAnotherNotPresent(doc, "//form", "email", "subscribe"));
        }

        private static string[] GetTextsBySelector(HtmlDocument doc, string xpath) {
            HtmlNode[] nodes = doc.DocumentNode.SelectNodes(xpath)?.ToArray() ?? Array.Empty<HtmlNode>();
            return nodes.Select(linkNode => linkNode.InnerText).Distinct().Where(s => !string.IsNullOrEmpty(s)).ToArray();
        }
        private static string[] GetAttributeValues(HtmlDocument doc, string attribute) {
            HtmlNode[] nodes = doc.DocumentNode.SelectNodes($"//@{attribute}")?.ToArray() ?? Array.Empty<HtmlNode>();
            return nodes.Select(linkNode => linkNode.GetAttributeValue(attribute, "")).Distinct().Where(s => !string.IsNullOrEmpty(s)).ToArray();
        }

        private static bool IsWordPresentAndAnotherNotPresent(HtmlDocument doc, string xpath, string word, string wordToFilterOut) {
            HtmlNode[] nodes = doc.DocumentNode.SelectNodes(xpath)?.ToArray() ?? Array.Empty<HtmlNode>();
            return nodes.Select(node => node.InnerHtml.ToLower()).Any(x => x.Contains(word) && !x.Contains(wordToFilterOut));
        }

        private static string[] FilterEmails(this IEnumerable<string> potentialEmails) {
            return potentialEmails
                .Select(text => RemoveWhitespace(emailRegex.Match(text?.ToLower() ?? "").Value))
                .Where(email => !string.IsNullOrEmpty(email) && !email.Contains('/'))
                .Distinct()
                .ToArray();
        }

        private static string RemoveWhitespace(string input) {
            return new string(input.ToCharArray()
                .Where(c => !char.IsWhiteSpace(c))
                .ToArray());
        }
        private static string ReplaceSimpleAntiScrapping(string html) {
            html = html.Replace(" at ", "@");
            html = html.Replace(" dot ", ".");
            html = html.Replace(" (at) ", "@");
            html = html.Replace("(at)", "@");
            html = html.Replace(" (dot) ", ".");
            html = html.Replace("(dot)", ".");
            html = html.Replace(" [at] ", "@");
            html = html.Replace("[at]", "@");
            html = html.Replace(" [dot] ", ".");
            html = html.Replace("[dot]", ".");
            return html;
        }
        private async static Task<string> GetHttpContentAsync(string absoluteUri) {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(HttpTimeout);
            using HttpClient client = new();
            using HttpResponseMessage response = await client.GetAsync(absoluteUri, cts.Token);
            using HttpContent content = response.Content;
            return await content.ReadAsStringAsync();
        }
    }
}
