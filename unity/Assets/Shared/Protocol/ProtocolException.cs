using System;

namespace RackScanner.Shared.Protocol
{
    public class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }
}
