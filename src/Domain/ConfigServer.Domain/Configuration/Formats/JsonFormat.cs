using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConfigServer.Domain.Configuration.Formats
{
    internal static class JsonFormat
    {
        internal static readonly JsonDocumentOptions DocumentOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static JsonNode Parse(string content)
        {
            try
            {
                return JsonNode.Parse(content, null, DocumentOptions);
            }
            catch (JsonException ex)
            {
                int? line = ex.LineNumber.HasValue ? (int?)(ex.LineNumber.Value + 1) : null;
                int? column = ex.BytePositionInLine.HasValue ? (int?)(ex.BytePositionInLine.Value + 1) : null;
                throw new ConfigFormatException("Invalid JSON: " + ex.Message, line, column, ex);
            }
            catch (ArgumentException ex)
            {
                throw new ConfigFormatException("Invalid JSON: " + ex.Message, null, null, ex);
            }
        }

        public static string Render(JsonNode tree)
            => tree == null ? "null" : tree.ToJsonString(WriteOptions);
    }
}
