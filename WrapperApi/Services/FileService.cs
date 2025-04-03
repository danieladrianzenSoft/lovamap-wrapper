namespace WrapperApi.Services
{
    public static class FileService
    {
        private static readonly string[] AllowedExtensions = new[] { ".json", ".csv", ".dat" };

        public static async Task<string> SaveUploadedFileAsync(IFormFile file, string directory)
        {
            var ext = Path.GetExtension(file.FileName).ToLower();

            if (!AllowedExtensions.Contains(ext))
                throw new InvalidOperationException("Unsupported file type.");

            var fileName = $"{Guid.NewGuid()}{ext}";
            var filePath = Path.Combine(directory, fileName);

            using var stream = File.Create(filePath);
            await file.CopyToAsync(stream);

            return fileName;
        }

        public static bool DeleteInputFile(string inputDir, string fileName)
        {
            var inputPath = Path.Combine(inputDir, fileName);

            if (File.Exists(inputPath))
            {
                try
                {
                    File.Delete(inputPath);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to delete input file {inputPath}: {ex.Message}");
                }
            }

            return false;
        }

        public static int DeleteMatchingOutputDirs(string outputDir, string fileName)
        {
            int deleted = 0;

            // Strip extension
            var baseFileName = Path.GetFileNameWithoutExtension(fileName);
            var pattern = $"{baseFileName}_";

            var matchingDirs = Directory.GetDirectories(outputDir)
                .Where(d => Path.GetFileName(d).StartsWith(pattern));

            foreach (var dir in matchingDirs)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    deleted++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to delete output dir {dir}: {ex.Message}");
                }
            }

            return deleted;
        }
    }
}

