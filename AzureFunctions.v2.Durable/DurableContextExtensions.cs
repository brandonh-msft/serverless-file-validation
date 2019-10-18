using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json.Linq;

namespace FileValidation
{
    static class DurableContextExtensions
    {
        public static void Log(this DurableOrchestrationContext context, TraceWriter log, string messsage, bool onlyIfNotReplaying = true)
        {
            if (!onlyIfNotReplaying || !context.IsReplaying)
            {
                log.Warning(messsage);
            }
        }

        public static void Log(this DurableOrchestrationClient client, TraceWriter log, string messsage, bool onlyIfNotReplaying = true) => log.Warning(messsage);

        public static JToken GetInputAsJson(this DurableActivityContextBase ctx) => ctx.GetInput<JToken>();

        public static JToken GetInputAsJson(this DurableOrchestrationContextBase ctx) => ctx.GetInput<JToken>();
    }
}
