using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Heleus.Base
{
    class WriteableOnlyContractResolver : DefaultContractResolver
    {
        readonly HashSet<string> _ignoredProperties = new HashSet<string>();

        public WriteableOnlyContractResolver(string[] ignoreList = null)
        {
            if (ignoreList != null)
                _ignoredProperties = new HashSet<string>(ignoreList);
        }

        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
        {
            var properties = base.CreateProperties(type, memberSerialization);
            properties = properties.Where(p => p.Writable && !_ignoredProperties.Contains(p.PropertyName)).ToList();
            return properties;
        }
    }

    public static class Json
    {
        public static string ToJson(object data)
        {
            return JsonConvert.SerializeObject(data);
        }

        public static string ToNiceJson(object data)
        {
            return JsonConvert.SerializeObject(data, Formatting.Indented);
        }

        public static string ToJson(object data, params string[] ignoreProperties)
        {
            return JsonConvert.SerializeObject(data, Formatting.None, new JsonSerializerSettings { ContractResolver = new WriteableOnlyContractResolver(ignoreProperties) });
        }

        public static T ToObject<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json);
        }

        public static object ToObject(Type type, string json)
        {
            return JsonConvert.DeserializeObject(json, type);
        }
    }
}
