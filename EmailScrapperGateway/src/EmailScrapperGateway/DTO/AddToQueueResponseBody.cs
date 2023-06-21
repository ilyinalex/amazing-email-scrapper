namespace EmailScrapperGateway.DTO {
    internal class AddToQueueResponseBody {
        public string[] QueuedDomains = Array.Empty<string>();
        public UriInfo[] DataFromCache = Array.Empty<UriInfo>();
        public UserInfo UserInfo = new UserInfo();
    }
}
