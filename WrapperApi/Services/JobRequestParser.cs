
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

        public static bool ParseGenerateMesh(IFormCollection form, bool defaultValue = true)
        {
            string? raw = null;

            if (form.TryGetValue("generateMesh", out var generateMeshValue))
                raw = generateMeshValue.ToString();
            else if (form.TryGetValue("meshProcessing", out var meshProcessingValue))
                raw = meshProcessingValue.ToString();

            if (string.IsNullOrWhiteSpace(raw))
                return defaultValue;

            raw = raw.Trim();

            if (bool.TryParse(raw, out var parsedBool))
                return parsedBool;

            if (int.TryParse(raw, out var parsedInt))
                return parsedInt != 0;

            var lowered = raw.ToLowerInvariant();
            return lowered is "y" or "yes" ? true
                 : lowered is "n" or "no" ? false
                 : defaultValue;
        }
    }
}
