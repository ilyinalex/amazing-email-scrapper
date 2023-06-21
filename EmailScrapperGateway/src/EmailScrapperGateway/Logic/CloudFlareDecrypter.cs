namespace EmailScrapperGateway.Logic {
    internal static class CloudFlareDecrypter {
        public const string urlFragment = "/cdn-cgi/l/email-protection#";
        private static int ExtractHex(string s, int index) => Convert.ToInt32("0x" + s.Substring(index, 2), 16);
        public static string Decrypt(string s) {
            string output = "";
            var key = ExtractHex(s, 0);
            for (var i = 2; i < s.Length; i += 2) {
                var u = ExtractHex(s, i) ^ key;
                output += (char)u;
            }
            return output;
        }
    }
}
