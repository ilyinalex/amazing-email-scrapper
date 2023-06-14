namespace EmailScrapperGateway.DTO
{
    internal class AddToQueueResponseBody
    {
        public string[] QueuedDomains;
        public UriInfo[] DataFromCache;
    }
}
