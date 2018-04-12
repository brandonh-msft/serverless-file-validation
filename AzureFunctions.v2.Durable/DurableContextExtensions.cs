using Microsoft.Azure.WebJobs.Host;

namespace FileValidation
{
    static class DurableContextExtensions
    {
        public static void Log(this Microsoft.Azure.WebJobs.DurableOrchestrationContext context, TraceWriter log, string messsage, bool onlyIfNotReplaying = true)
        {
            if (!onlyIfNotReplaying || !context.IsReplaying)
            {
                log.Warning(messsage);
            }
        }

        public static void Log(this Microsoft.Azure.WebJobs.DurableOrchestrationClient client, TraceWriter log, string messsage, bool onlyIfNotReplaying = true) => log.Warning(messsage);
    }
}
