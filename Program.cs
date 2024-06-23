#pragma warning disable CA1416
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImageMagick;
using nQuant;
using RectpackSharp;

internal class Program
{
    struct FrameInfo
    {
        public int Height;
        public int Width;
        public int OffsetX;
        public int OffsetY;

        public FrameInfo(int height, int width, int offsetX, int offsetY)
        {
            Height = height;
            Width = width;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }
    }

    struct CroppedImgInfo
    {
        public string Path;
        public uint Width;
        public uint Height;
    }
    
    public static void Main(string[] args)
    {
        foreach (var arg in args)
        {
            Console.WriteLine($"Checking arg: {arg}");
            if (Directory.Exists(arg))
            {
                ProcessFolder(new DirectoryInfo(arg));
            }
        }
        Console.WriteLine("Done. Press any key to close.");
        Console.Read();
    }

    static void ProcessFolder(DirectoryInfo dir)
    {
        Console.WriteLine($"Processing folder: {dir}");
        if (!File.Exists(dir.FullName + "/meta.json"))
        {
            Console.WriteLine("No meta.json found.");
            return;
        }

        List<FrameInfo> frameInfos = new List<FrameInfo>();
        FrameInfo animInfo;

        // ASSET TOOL META.JSON
        using (var meta = JsonDocument.Parse(File.ReadAllText(dir.FullName + "/meta.json")))
        {
            try
            {
                var root = meta.RootElement;
                
                var mainHeight = root.GetProperty("size").GetProperty("h").GetInt16();
                var mainWidth = root.GetProperty("size").GetProperty("w").GetInt16();
                var mainOffsetX = root.GetProperty("offset").GetProperty("x").GetInt16();
                var mainOffsetY = root.GetProperty("offset").GetProperty("y").GetInt16();
                animInfo = new FrameInfo(mainHeight, mainWidth, mainOffsetX, mainOffsetY);
                
                var frameCount = root.GetProperty("frame_count").GetInt16();
                var framesInfo = root.GetProperty("extra").GetProperty("frames_info");
                
                Console.WriteLine($"{frameCount} frames");
                foreach (var frame in framesInfo.EnumerateArray())
                {
                    var height = frame.GetProperty("original_height").GetInt16() * 2; //double height sprites
                    var width = frame.GetProperty("original_width").GetInt16();
                    var offsetX = frame.GetProperty("x_offset").GetInt16();
                    var offsetY = frame.GetProperty("y_offset").GetInt16() * 2; //double height sprites
                    
                    var info = new FrameInfo(height, width, offsetX, offsetY);
                    frameInfos.Add(info);
                }

                if (frameInfos.Count != frameCount)
                {
                    Console.WriteLine($"meta.json: Frame info count ({frameInfos.Count}) does not match frame_count value ({frameCount})!");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"{dir.Name}: Error while reading meta.json - {e}");
                return;
            }
        }

        var croppedImages = new List<MagickImage>();
        
        var rectangles = new PackingRectangle[frameInfos.Count];
        for (int i = 0; i < frameInfos.Count; i++)
        {
            var inputName = dir.FullName + "/" + i + ".png";
            var outputName = dir.FullName + "/crop_" + i + ".png";
            var croppedImage = CropImage(inputName, outputName, frameInfos[i], animInfo);
            croppedImages.Add(new MagickImage(croppedImage.Path));
            rectangles[i].Width = croppedImage.Width;
            rectangles[i].Height = croppedImage.Height;
            rectangles[i].Id = i;
        }
        
        Console.WriteLine($"{dir.Name} frames have been cropped.");

        var appDir = AppContext.BaseDirectory;

        foreach (var subDir in Directory.GetParent(appDir).Parent.EnumerateDirectories())
        {
            Console.WriteLine($"Looking for json in {subDir}...");
            var jsonPath = subDir + "/" + dir.Name + ".json";
            var jsonBackupPath = subDir + "/" + dir.Name + "_backup.json";
            if (File.Exists(jsonPath))
            {
                RectanglePacker.Pack(rectangles, out var bounds);
                var sortedRects = rectangles.OrderBy(r => r.Id).ToList();
                var spriteSheet = new MagickImage(new MagickColor("#00000000"), (int)bounds.Width, (int)bounds.Height);
                
                for (int i = 0; i < frameInfos.Count; i++)
                {
                    spriteSheet.Composite(croppedImages[i], (int)sortedRects[i].X, (int)sortedRects[i].Y, CompositeOperator.Copy, Channels.All);
                }

                var fullColorSheetPath = subDir + "/" + "NOPAL_" + dir.Name + ".png";
                var indexedSheetPath = subDir + "/" + dir.Name + ".png";
                spriteSheet.Write(fullColorSheetPath);

                var quantizer = new WuQuantizer();
                using(var bitmap = new Bitmap(fullColorSheetPath))
                {
                    using (var dest = quantizer.QuantizeImage(bitmap, 0,127))
                    {
                        // force alpha values to be 0, 127 or 255
                        const int fullAlphaThreshold = 127;
                        var newPalette = dest.Palette;
                        for (int i = 0; i < newPalette.Entries.Length; i++)
                        {
                            var col = newPalette.Entries[i];
                            if (col.A is > 0 and < 255)
                            {
                                newPalette.Entries[i] = Color.FromArgb(col.A > fullAlphaThreshold ? 255 : 127,col.R,col.G,col.B);
                            }
                        }

                        dest.Palette = newPalette;
                        dest.Save(indexedSheetPath, ImageFormat.Png);
                    }
                }
                
                Console.WriteLine($"{dir.Name} spritesheet has been saved.");

                if (!File.Exists(jsonBackupPath))
                {
                    File.Copy(jsonPath, jsonBackupPath);
                }

                var json = JsonNode.Parse(File.ReadAllText(jsonPath))!;
                for (int i = 0; i < sortedRects.Count; i++)
                {
                    json["frames"][i]["sprite_height"] = sortedRects[i].Height;
                    json["frames"][i]["sprite_width"] = sortedRects[i].Width;
                    json["frames"][i]["sprite_sheet_x"] = sortedRects[i].X;
                    json["frames"][i]["sprite_sheet_y"] = sortedRects[i].Y;
                }

                //JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true, IndentSize = 4 }); // .Net 9 only :(
                File.WriteAllText(jsonPath, json.ToString());
                
                Console.WriteLine($"{dir.Name} json has been updated.");
                break;
            }
        }
    }

    static CroppedImgInfo CropImage(string inputImagePath, string outputImagePath, FrameInfo frame, FrameInfo anim)
    {
        Console.WriteLine($"Cropping image: {inputImagePath}");
        using var image = new MagickImage(inputImagePath);
        var xScale = (float)image.Width / anim.Width;
        var yScale = (float)image.Height / anim.Height;

        var cropX = xScale * (anim.OffsetX + frame.OffsetX);
        var cropY = yScale * (anim.OffsetY + frame.OffsetY);
        var cropW = xScale * frame.Width;
        var cropH = yScale * frame.Height;
        
        image.Crop(new MagickGeometry((int)cropX, (int)cropY, (int)cropW, (int)cropH));
        image.Write(outputImagePath);
        
        var croppedImg = new CroppedImgInfo
        {
            Path = outputImagePath,
            Width = (uint)cropW,
            Height = (uint)cropH
        };
        return croppedImg;
    }
}