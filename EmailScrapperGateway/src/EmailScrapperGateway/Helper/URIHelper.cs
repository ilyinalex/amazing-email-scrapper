using System;

namespace EmailScrapperGateway.Helper
{
    internal static class URIHelper
    {
        public static (string? absoluteUri, string? domain) GetUri(string? initialUri)
        {
            if (initialUri == null) { return (null, null); }
            Uri uri;
            try
            {
                uri = new UriBuilder(initialUri.Trim()).Uri;
            }
            catch
            {
                return (null, null);
            }
            string absoluteUri = uri.AbsoluteUri;
            if (absoluteUri[..7] == @"http://")
            {
                absoluteUri = "https://" + absoluteUri[7..];
            }
            string domain = uri.Host;
            if (domain.StartsWith("www."))
            {
                domain = domain[4..];
            }
            return (absoluteUri.ToLower(), domain.ToLower());
        }
        public static List<string> GetInternalUris(IEnumerable<string> hrefList, string domain, string absoluteUri)
        {
            List<string> internalUris = new List<string>();
            foreach (string href in hrefList)
            {
                string internalAbsoluteLinkText = href;
                if (!href.Contains(domain))
                {
                    if (href.StartsWith("http") || href.StartsWith("#")) { continue; }
                    internalAbsoluteLinkText = absoluteUri.TrimEnd('/') + "/" + href.TrimStart('/');
                }
                if (GetUri(internalAbsoluteLinkText).domain?.ToLower() != domain.ToLower()) { continue; }
                internalUris.Add(internalAbsoluteLinkText);
            }
            return internalUris;
        }
    }
}
