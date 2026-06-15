namespace WrapperApi.Services
{
    public static class FileService
    {
        private static readonly string[] AllowedExtensions = new[] { ".json", ".csv", ".dat", ".txt" };
        private static readonly string[] ImageExtensions = new[] { ".tif", ".tiff", ".lif", ".mat" };
        private const long MaxFileSizeBytes = 100 * 1024 * 1024; // 100 MB — must match FormOptions.MemoryBufferThreshold

        public static async Task<string> SaveUploadedFileAsync(IFormFile file, string directory, bool allowImageFiles = false)
        {
            var ext = Path.GetExtension(file.FileName).ToLower();

            var allowed = allowImageFiles ? AllowedExtensions.Concat(ImageExtensions) : AllowedExtensions;
            if (!allowed.Contains(ext))
                throw new InvalidOperationException("Unsupported file type.");

            if (file.Length > MaxFileSizeBytes)
            {
                var sizeMB = file.Length / (1024.0 * 1024.0);
                var limitMB = MaxFileSizeBytes / (1024.0 * 1024.0);
                Console.WriteLine($"[ERROR] File too large: {sizeMB:F1} MB exceeds {limitMB:F0} MB limit for {file.FileName}");
                throw new InvalidOperationException($"File too large ({sizeMB:F1} MB). Maximum allowed size is {limitMB:F0} MB.");
            }

            var fileName = $"{Guid.NewGuid()}{ext}";
            var filePath = Path.Combine(directory, fileName);

            // Read entire file into memory then write to disk in one shot
            // to avoid partial-write corruption on volume mounts
            byte[] fileBytes;
            using (var inputStream = file.OpenReadStream())
            using (var memoryStream = new MemoryStream((int)file.Length))
            {
                await inputStream.CopyToAsync(memoryStream);
                fileBytes = memoryStream.ToArray();
            }

            Console.WriteLine($"[UPLOAD] Read {fileBytes.Length} bytes into memory for {file.FileName} (expected {file.Length})");

            await File.WriteAllBytesAsync(filePath, fileBytes);

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

        public static string ResolveSourceJobFile(string sourceFilePath, string inputDir)
        {
            if (!File.Exists(sourceFilePath))
                throw new FileNotFoundException($"Source job output file not found: {sourceFilePath}");

            var ext = Path.GetExtension(sourceFilePath);
            var fileName = $"{Guid.NewGuid()}{ext}";
            var destPath = Path.Combine(inputDir, fileName);

            File.Copy(sourceFilePath, destPath);

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
