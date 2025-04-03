
namespace WrapperApi.Services 
{
    public static class JobRequestParser
    {
        public static string? ParseJobId(IFormCollection form)
        {
            if (form.TryGetValue("jobId", out var jobIdValue))
            {
                var jobId = jobIdValue.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(jobId))
                    return jobId;
            }
            return null;
        }

        public static string ParseDxValue(IFormCollection form, string defaultValue = "4.0")
        {
            return form.TryGetValue("dx", out var dxValue) ? dxValue.ToString() : defaultValue;
        }
    }
}

