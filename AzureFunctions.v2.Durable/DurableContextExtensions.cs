using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace FileValidation
{
    static class DurableContextExtensions
    {
        public static void Log(this DurableOrchestrationContext context, ILogger log, string messsage, bool onlyIfNotReplaying = true)
        {
            if (!onlyIfNotReplaying || !context.IsReplaying)
            {
                log.LogWarning(messsage);
            }
        }

        public static void Log(this DurableOrchestrationClient _, ILogger log, string messsage) => log.LogWarning(messsage);

        public static JToken GetInputAsJson(this DurableActivityContextBase ctx) => ctx.GetInput<JToken>();

        public static JToken GetInputAsJson(this DurableOrchestrationContextBase ctx) => ctx.GetInput<JToken>();
    }
}
