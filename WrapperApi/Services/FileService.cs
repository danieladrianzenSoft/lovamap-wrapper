namespace WrapperApi.Services
{
    public static class FileService
    {
        private static readonly string[] AllowedExtensions = new[] { ".json", ".csv", ".dat" };
        private static readonly string[] ImageExtensions = new[] { ".tif", ".tiff", ".lif", ".mat" };

        public static async Task<string> SaveUploadedFileAsync(IFormFile file, string directory, bool allowImageFiles = false)
        {
            var ext = Path.GetExtension(file.FileName).ToLower();

            var allowed = allowImageFiles ? AllowedExtensions.Concat(ImageExtensions) : AllowedExtensions;
            if (!allowed.Contains(ext))
                throw new InvalidOperationException("Unsupported file type.");

            var fileName = $"{Guid.NewGuid()}{ext}";
            var filePath = Path.Combine(directory, fileName);

            using (var inputStream = file.OpenReadStream())
            using (var outputStream = File.Create(filePath))
            {
                await inputStream.CopyToAsync(outputStream);
                await outputStream.FlushAsync();
            }

            // Verify the file was written correctly
            var writtenSize = new FileInfo(filePath).Length;
            if (writtenSize != file.Length)
            {
                Console.WriteLine($"[ERROR] File size mismatch: expected {file.Length}, got {writtenSize} for {filePath}");
                File.Delete(filePath);
                throw new InvalidOperationException($"File upload failed: size mismatch (expected {file.Length}, wrote {writtenSize}).");
            }

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

