using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DSharpPlus.Net.Serialization
{
    /// <summary>
    /// Keeps a JSON value as its raw text instead of building objects from it. Copying the text is much cheaper than
    /// creating objects through reflection (.NET Native on the phone); the owner turns it into objects when needed.
    /// </summary>
    internal sealed class RawJsonStringConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(string);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;
            var raw = JRaw.Create(reader);
            return raw.Value as string ?? raw.ToString(Formatting.None);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is string json)
                writer.WriteRawValue(json);
            else
                writer.WriteNull();
        }
    }
}
