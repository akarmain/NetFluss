// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetFluss.Core;

/// <summary>
/// Enums as their names, like <see cref="JsonStringEnumConverter"/> — except that a name
/// this build does not know reads as the property's default instead of failing the whole
/// document. A settings file written by a newer NetFluss (a style added later), or edited by
/// hand, then costs one preference rather than all of them.
/// </summary>
public sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class LenientEnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : default;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
            {
                var value = (T)Enum.ToObject(typeof(T), number);
                return Enum.IsDefined(value) ? value : default;
            }

            reader.Skip();
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
