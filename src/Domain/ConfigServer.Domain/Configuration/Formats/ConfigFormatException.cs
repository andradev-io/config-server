using System;

namespace ConfigServer.Domain.Configuration.Formats
{
    /// <summary>Thrown when configuration text cannot be parsed or rendered. Line and Column are 1-based when known.</summary>
    public class ConfigFormatException : Exception
    {
        public int? Line { get; }
        public int? Column { get; }

        public ConfigFormatException(string message, int? line = null, int? column = null, Exception innerException = null)
            : base(message, innerException)
        {
            Line = line;
            Column = column;
        }

        public ConfigFormatException WithPrefix(string prefix)
            => new ConfigFormatException(prefix + Message, Line, Column, this);
    }
}
