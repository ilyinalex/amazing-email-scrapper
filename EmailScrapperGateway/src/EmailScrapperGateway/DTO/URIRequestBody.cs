﻿namespace EmailScrapperGateway.DTO {
    internal class URIRequestBody {
        public string[] URIs = Array.Empty<string>();
        public string User = "";
        public UserInfo UserInfo = new UserInfo();
    }
}
