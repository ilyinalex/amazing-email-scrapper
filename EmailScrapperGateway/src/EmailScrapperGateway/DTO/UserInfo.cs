namespace EmailScrapperGateway.DTO {
    internal class UserInfo {
        public string User = "";
        public List<string> RequestedDomains = new List<string>();
        public List<string> FoundDomains = new List<string>();
        public int AllowedDomainRequestCount;
        public int RemainingDomainRequestCount;
    }
}
