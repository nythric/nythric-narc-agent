using System;

namespace NarcAgent.Proxy.Services.Nginx.Exceptions
{
    public class NginxOrphanConfigException : Exception
    {
        public string ProxyId { get; }
        public string Domain { get; }

        public NginxOrphanConfigException(string proxyId, string domain, string message)
            : base(message)
        {
            ProxyId = proxyId;
            Domain = domain;
        }
    }
}
