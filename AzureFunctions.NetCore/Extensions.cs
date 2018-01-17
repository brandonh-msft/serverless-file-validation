using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace Gatekeeper
{
    static class EnumExtensions
    {
        public static IEnumerable<Enum> GetFlags(this Enum value)
        {
            return GetFlags(value, Enum.GetValues(value.GetType()).Cast<Enum>().ToArray());
        }

        public static IEnumerable<Enum> GetIndividualFlags(this Enum value)
        {
            return GetFlags(value, GetFlagValues(value.GetType()).ToArray());
        }

        private static IEnumerable<Enum> GetFlags(Enum value, Enum[] values)
        {
            ulong bits = Convert.ToUInt64(value);
            List<Enum> results = new List<Enum>();
            for (int i = values.Length - 1; i >= 0; i--)
            {
                ulong mask = Convert.ToUInt64(values[i]);
                if (i == 0 && mask == 0L)
                    break;
                if ((bits & mask) == mask)
                {
                    results.Add(values[i]);
                    bits -= mask;
                }
            }
            if (bits != 0L)
                return Enumerable.Empty<Enum>();
            if (Convert.ToUInt64(value) != 0L)
                return results.Reverse<Enum>();
            if (bits == Convert.ToUInt64(value) && values.Length > 0 && Convert.ToUInt64(values[0]) == 0L)
                return values.Take(1);
            return Enumerable.Empty<Enum>();
        }

        private static IEnumerable<Enum> GetFlagValues(Type enumType)
        {
            ulong flag = 0x1;
            foreach (var value in Enum.GetValues(enumType).Cast<Enum>())
            {
                ulong bits = Convert.ToUInt64(value);
                if (bits == 0L)
                    //yield return value;
                    continue; // skip the zero value
                while (flag < bits) flag <<= 1;
                if (flag == bits)
                    yield return value;
            }
        }
    }

    static class StorageExtensions
    {
        public static async System.Threading.Tasks.Task<IEnumerable<DynamicTableEntity>> ExecuteQueryAsync(this CloudTable table, TableQuery query)
        {
            TableContinuationToken token = null;
            var retVal = new List<DynamicTableEntity>();
            do
            {
                var results = await table.ExecuteQuerySegmentedAsync(query, token);
                retVal.AddRange(results.Results);
                token = results.ContinuationToken;
            } while (token != null);

            return retVal;
        }

        public static async System.Threading.Tasks.Task<IEnumerable<T>> ExecuteQueryAsync<T>(this CloudTable table, TableQuery<T> query) where T : ITableEntity, new()
        {
            TableContinuationToken token = null;
            var retVal = new List<T>();
            do
            {
                var results = await table.ExecuteQuerySegmentedAsync(query, token);
                retVal.AddRange(results.Results);
                token = results.ContinuationToken;
            } while (token != null);

            return retVal;
        }


        public static async System.Threading.Tasks.Task<IEnumerable<IListBlobItem>> ListBlobsAsync(this CloudBlobClient blobClient, string prefix)
        {
            BlobContinuationToken token = null;
            var retVal = new List<IListBlobItem>();
            do
            {
                var results = await blobClient.ListBlobsSegmentedAsync(prefix, token);
                retVal.AddRange(results.Results);
                token = results.ContinuationToken;
            } while (token != null);

            return retVal;
        }

    }

    static class HttpExtensions
    {
        public static HttpResponseMessage CreateCompatibleResponse(this HttpRequestMessage req, HttpStatusCode code) => new HttpResponseMessage(code);

        public static HttpResponseMessage CreateCompatibleResponse(this HttpRequestMessage req, HttpStatusCode code, string stringContent) => new HttpResponseMessage(code) { Content = new StringContent(stringContent) };

        public static HttpResponseMessage CreateCompatibleResponse<T>(this HttpRequestMessage req, HttpStatusCode code, T content) => new HttpResponseMessage(code) { Content = new ObjectContent(typeof(T), content, new System.Net.Http.Formatting.JsonMediaTypeFormatter(), @"application/json") };
    }
}
