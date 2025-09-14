using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace PhotoViewer.Core;

/// <summary>
/// XMP Writer for modifying single XMP Rating character in JPG files
/// </summary>
public static class XmpWriter
{
    private const byte JpegMarkerStart = 0xFF;
    private const byte App1Marker = 0xE1;
    
    /// <summary>
    /// Write XMP Rating to JPG file by modifying only the rating digit
    /// </summary>
    /// <param name="file">Storage file to modify</param>
    /// <param name="rating">Rating value (0-5)</param>
    /// <returns>True if successful, false if file doesn't meet requirements</returns>
    public static async Task<bool> WriteRatingAsync(IStorageFile file, int rating)
    {
        // Validate input parameters
        if (rating < 0 || rating > 5)
        {
            Console.WriteLine($"XmpWriter: Invalid rating value {rating}, must be between 0-5");
            return false;
        }
        
        var filePath = file.Path.LocalPath;
        if (!filePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && 
            !filePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"XmpWriter: File {file.Name} is not a JPG file");
            return false;
        }
        
        try
        {
            // Read original file data
            byte[] originalData;
            await using (var stream = await file.OpenReadAsync())
            {
                originalData = new byte[stream.Length];
                await stream.ReadAsync(originalData, 0, originalData.Length);
            }
            
            Console.WriteLine($"XmpWriter: Processing file {file.Name}, size: {originalData.Length} bytes");
            
            // Find XMP segment and rating position
            var ratingPosition = FindXmpRatingPosition(originalData);
            if (ratingPosition == -1)
            {
                Console.WriteLine($"XmpWriter: No XMP Rating found in {file.Name}");
                return false;
            }
            
            // Get current rating value
            var currentRating = originalData[ratingPosition] - '0';
            if (currentRating < 0 || currentRating > 5)
            {
                Console.WriteLine($"XmpWriter: Invalid current rating {currentRating} at position {ratingPosition}");
                return false;
            }
            
            Console.WriteLine($"XmpWriter: Found rating {currentRating} at position {ratingPosition}");
            
            // If rating is the same, no need to modify
            if (currentRating == rating)
            {
                Console.WriteLine($"XmpWriter: Rating is already {rating}, no changes needed");
                return true;
            }
            
            // Create modified data by changing only the rating digit
            var modifiedData = new byte[originalData.Length];
            Array.Copy(originalData, modifiedData, originalData.Length);
            modifiedData[ratingPosition] = (byte)('0' + rating);
            
            Console.WriteLine($"XmpWriter: Changing rating from {currentRating} to {rating} at position {ratingPosition}");
            
            // Verify that only one byte changed
            if (!VerifyOnlyRatingChanged(originalData, modifiedData, ratingPosition))
            {
                Console.WriteLine($"XmpWriter: Verification failed - more than one byte changed");
                return false;
            }
            
            // Write modified data to temporary file first
            var tempFilePath = filePath + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(tempFilePath, modifiedData);
                
                // Verify written file
                var writtenData = await File.ReadAllBytesAsync(tempFilePath);
                if (!writtenData.SequenceEqual(modifiedData))
                {
                    Console.WriteLine($"XmpWriter: Written file verification failed");
                    File.Delete(tempFilePath);
                    return false;
                }
                
                // Replace original file atomically
                File.Replace(tempFilePath, filePath, null);
                Console.WriteLine($"XmpWriter: Successfully updated rating from {currentRating} to {rating} for {file.Name}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"XmpWriter: Failed to write file: {ex.Message}");
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"XmpWriter: Unexpected error processing {file.Name}: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Find the position of XMP Rating digit in the file
    /// </summary>
    private static int FindXmpRatingPosition(byte[] data)
    {
        try
        {
            // Find APP1 XMP segment
            for (int i = 0; i < data.Length - 1; i++)
            {
                if (data[i] == JpegMarkerStart && data[i + 1] == App1Marker)
                {
                    Console.WriteLine($"XmpWriter: Found APP1 marker at position {i}");
                    
                    // Check if this is an XMP segment
                    if (i + 4 >= data.Length) continue;
                    
                    var segmentLength = (data[i + 2] << 8) | data[i + 3];
                    var segmentStart = i + 4;
                    var segmentEnd = segmentStart + segmentLength - 2;
                    
                    if (segmentEnd > data.Length) continue;
                    
                    // Check for XMP identifier
                    var xmpIdentifier = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
                    if (segmentLength < xmpIdentifier.Length + 2) continue;
                    
                    bool isXmpSegment = true;
                    for (int j = 0; j < xmpIdentifier.Length; j++)
                    {
                        if (segmentStart + j >= data.Length || data[segmentStart + j] != xmpIdentifier[j])
                        {
                            isXmpSegment = false;
                            break;
                        }
                    }
                    
                    if (!isXmpSegment) continue;
                    
                    Console.WriteLine($"XmpWriter: Found XMP segment at position {segmentStart}, length {segmentLength}");
                    
                    // Search for rating patterns in this XMP segment
                    var xmpDataStart = segmentStart + xmpIdentifier.Length;
                    var xmpDataEnd = segmentEnd;
                    
                    var ratingPos = FindRatingInXmpData(data, xmpDataStart, xmpDataEnd);
                    if (ratingPos != -1)
                    {
                        return ratingPos;
                    }
                }
            }
            
            Console.WriteLine("XmpWriter: No XMP Rating found in any APP1 segment");
            return -1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"XmpWriter: Error finding XMP rating position: {ex.Message}");
            return -1;
        }
    }
    
    /// <summary>
    /// Find rating digit within XMP data
    /// </summary>
    private static int FindRatingInXmpData(byte[] data, int start, int end)
    {
        try
        {
            // Common XMP rating patterns to search for
            var ratingPatterns = new[]
            {
                "xmp:Rating=\"",
                "xap:Rating=\"", 
                ":Rating=\"",
                "Rating=\"",
                "<xmp:Rating>",
                "<xap:Rating>",
                "<Rating>"
            };
            
            foreach (var pattern in ratingPatterns)
            {
                var patternBytes = Encoding.UTF8.GetBytes(pattern);
                
                for (int i = start; i <= end - patternBytes.Length - 1; i++)
                {
                    bool matches = true;
                    for (int j = 0; j < patternBytes.Length; j++)
                    {
                        if (data[i + j] != patternBytes[j])
                        {
                            matches = false;
                            break;
                        }
                    }
                    
                    if (matches)
                    {
                        var ratingPos = i + patternBytes.Length;
                        
                        // Verify the next character is a valid rating digit (0-5)
                        if (ratingPos < data.Length)
                        {
                            var ratingChar = data[ratingPos];
                            if (ratingChar >= '0' && ratingChar <= '5')
                            {
                                Console.WriteLine($"XmpWriter: Found rating pattern '{pattern}' at position {i}, rating digit at {ratingPos}");
                                return ratingPos;
                            }
                        }
                    }
                }
            }
            
            // Alternative search for XML element content
            var xmlPatterns = new[]
            {
                "</xmp:Rating>",
                "</xap:Rating>", 
                "</Rating>"
            };
            
            foreach (var endPattern in xmlPatterns)
            {
                var endPatternBytes = Encoding.UTF8.GetBytes(endPattern);
                
                for (int i = start; i <= end - endPatternBytes.Length; i++)
                {
                    bool matches = true;
                    for (int j = 0; j < endPatternBytes.Length; j++)
                    {
                        if (data[i + j] != endPatternBytes[j])
                        {
                            matches = false;
                            break;
                        }
                    }
                    
                    if (matches)
                    {
                        // Look backwards for the rating digit
                        var ratingPos = i - 1;
                        if (ratingPos >= start && data[ratingPos] >= '0' && data[ratingPos] <= '5')
                        {
                            Console.WriteLine($"XmpWriter: Found rating via end tag '{endPattern}' at position {i}, rating digit at {ratingPos}");
                            return ratingPos;
                        }
                    }
                }
            }
            
            return -1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"XmpWriter: Error searching for rating in XMP data: {ex.Message}");
            return -1;
        }
    }
    
    /// <summary>
    /// Verify that only the rating digit changed between original and modified data
    /// </summary>
    private static bool VerifyOnlyRatingChanged(byte[] originalData, byte[] modifiedData, int ratingPosition)
    {
        try
        {
            if (originalData.Length != modifiedData.Length)
            {
                Console.WriteLine($"XmpWriter: File length changed: {originalData.Length} -> {modifiedData.Length}");
                return false;
            }
            
            int changedBytes = 0;
            int firstChangedPosition = -1;
            
            for (int i = 0; i < originalData.Length; i++)
            {
                if (originalData[i] != modifiedData[i])
                {
                    changedBytes++;
                    if (firstChangedPosition == -1)
                    {
                        firstChangedPosition = i;
                    }
                    
                    if (changedBytes > 1)
                    {
                        Console.WriteLine($"XmpWriter: Multiple bytes changed. First at {firstChangedPosition}, another at {i}");
                        return false;
                    }
                }
            }
            
            if (changedBytes == 0)
            {
                Console.WriteLine($"XmpWriter: No bytes changed");
                return true; // Same rating, no change needed
            }
            
            if (changedBytes == 1 && firstChangedPosition == ratingPosition)
            {
                var oldRating = originalData[ratingPosition];
                var newRating = modifiedData[ratingPosition];
                Console.WriteLine($"XmpWriter: Verification passed - only rating digit changed at position {ratingPosition}: '{(char)oldRating}' -> '{(char)newRating}'");
                return true;
            }
            
            Console.WriteLine($"XmpWriter: Unexpected change at position {firstChangedPosition}, expected {ratingPosition}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"XmpWriter: Error during verification: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Read current XMP Rating from file without modifying it
    /// </summary>
    public static async Task<int?> ReadRatingAsync(IStorageFile file)
    {
        try
        {
            var filePath = file.Path.LocalPath;
            if (!filePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && 
                !filePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            
            byte[] data;
            await using (var stream = await file.OpenReadAsync())
            {
                data = new byte[stream.Length];
                await stream.ReadAsync(data, 0, data.Length);
            }
            
            var ratingPosition = FindXmpRatingPosition(data);
            if (ratingPosition == -1) return null;
            
            var ratingChar = data[ratingPosition];
            if (ratingChar >= '0' && ratingChar <= '5')
            {
                return ratingChar - '0';
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"XmpWriter: Error reading rating from {file.Name}: {ex.Message}");
            return null;
        }
    }
}

