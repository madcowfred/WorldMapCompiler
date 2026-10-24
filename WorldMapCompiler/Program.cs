using BLPSharp;
using Microsoft.Extensions.Configuration;
using NetVips;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TACTSharp;
using static TACTSharp.RootInstance;

namespace WorldMapCompiler;

internal class Program
{
    private static readonly VOption PngVOption = new()
    {
        { "compression", 5 },
        { "strip", true },
        { "interlace", false },
        { "filter", Enums.ForeignPngFilter.None }
    };

    private static void Main(string[] args)
    {
        var config = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("settings.json", true, true).Build();

        var saveExplored = bool.Parse(config["saveExploredMaps"]);
        var saveUnexplored = bool.Parse(config["saveUnexploredMaps"]);
        var saveLayers = bool.Parse(config["saveMapLayers"]);
        var saveExploredMapsWithoutUnexplored = bool.Parse(config["saveExploredMapsWithoutUnexplored"]);

        if (saveExplored && !Directory.Exists("explored"))
        {
            Directory.CreateDirectory("explored");
        }

        if (saveUnexplored && !Directory.Exists("unexplored"))
        {
            Directory.CreateDirectory("unexplored");
        }

        if (saveLayers && !Directory.Exists("layers"))
        {
            Directory.CreateDirectory("layers");
        }

        if (saveExploredMapsWithoutUnexplored && !Directory.Exists("exploredNoUnexplored"))
        {
            Directory.CreateDirectory("exploredNoUnexplored");
        }

        var locale = LocaleFlags.enUS;

        if (config["locale"] != string.Empty)
        {
            switch (config["locale"])
            {
                case "deDE":
                    locale = LocaleFlags.deDE;
                    break;
                case "enUS":
                    locale = LocaleFlags.enUS;
                    break;
                case "ruRU":
                    locale = LocaleFlags.ruRU;
                    break;
                case "zhCN":
                    locale = LocaleFlags.zhCN;
                    break;
                case "zhTW":
                    locale = LocaleFlags.zhTW;
                    break;
                case "frFR":
                    locale = LocaleFlags.frFR;
                    break;
            }
        }

        BuildInstance buildInstance = new();
        if (config["installDir"] != string.Empty && Directory.Exists(config["installDir"]))
        {
            buildInstance.LoadLocal(config["installDir"], config["program"]);
        }
        else
        {
            buildInstance.LoadOnline(config["program"]);
        }

        buildInstance.Settings.Locale = locale;

        var dbcd = new DBCD.DBCD(new CASCDBCProvider(buildInstance), new DBCD.Providers.GithubDBDProvider());

        string build;

        if (buildInstance.BuildConfig.Values["build-name"][0].StartsWith("WOW-"))
        {
            var buildNumber = buildInstance.BuildConfig.Values["build-name"][0].Split("patch")[0].Replace("WOW-", "");
            var buildName = buildInstance.BuildConfig.Values["build-name"][0].Split("patch")[1].Split('_')[0];
            build = buildName + "." + buildNumber;
        }
        else
        {
            build = buildInstance.BuildConfig.Values["build-name"][0];
        }

        var UIMap = dbcd.Load("UiMap", build);
        var UIMapXArt = dbcd.Load("UiMapXMapArt", build);
        var UIMapArtTile = dbcd.Load("UiMapArtTile", build);
        var UIMapArt = dbcd.Load("UiMapArt", build);
        var UIMapArtStyleLayer = dbcd.Load("UiMapArtStyleLayer", build);
        var WorldMapOverlay = dbcd.Load("WorldMapOverlay", build);
        var WorldMapOverlayTile = dbcd.Load("WorldMapOverlayTile", build);

        Console.WriteLine(); // new line after wdc2 debug output

        foreach (dynamic mapRow in UIMap.Values.Reverse())
        {
            var mapName = mapRow.Name_lang;
            var cleanMapName = CleanFileName($"{mapRow.ID} - {mapName}");

            Console.WriteLine(mapRow.ID + " = " + mapName);

            foreach (dynamic mxaRow in UIMapXArt.Values)
            {
                var uiMapArtID = mxaRow.UiMapArtID;
                var uiMapID = mxaRow.UiMapID;

                if (mxaRow.PhaseID != 0)
                    continue; // Skip phase stuff for now

                if (uiMapID != mapRow.ID)
                    continue;

                var maxRows = uint.MinValue;
                var maxCols = uint.MinValue;
                var tileDict = new Dictionary<string, int>();

                foreach (dynamic matRow in UIMapArtTile.Values)
                {
                    var matUiMapArtID = matRow.UiMapArtID;
                    if (matUiMapArtID == uiMapArtID)
                    {
                        var fdid = matRow.FileDataID;
                        var rowIndex = matRow.RowIndex;
                        var colIndex = matRow.ColIndex;
                        var layerIndex = matRow.LayerIndex;

                        // Skip other layers for now
                        if (layerIndex != 0)
                        {
                            continue;
                        }

                        if (rowIndex > maxRows)
                        {
                            maxRows = rowIndex;
                        }

                        if (colIndex > maxCols)
                        {
                            maxCols = colIndex;
                        }

                        tileDict.Add(rowIndex + "," + colIndex, fdid);
                    }
                }

                uint res_x = 0;
                uint res_y = 0;

                foreach (dynamic maRow in UIMapArt.Values)
                {
                    if (maRow.ID == uiMapArtID)
                    {
                        foreach (dynamic mastRow in UIMapArtStyleLayer.Values)
                        {
                            if (mastRow.ID == maRow.UiMapArtStyleID)
                            {
                                res_x = mastRow.LayerHeight;
                                res_y = mastRow.LayerWidth;
                                continue;
                            }
                        }

                        continue;
                    }
                }

                if (res_x == 0)
                {
                    res_x = (maxRows + 1) * 256;
                }

                if (res_y == 0)
                {
                    res_y = (maxCols + 1) * 256;
                }

                // TODO: add alpha?
                using var image1 = NewTransparentImage((int)res_y, (int)res_x);
                var image1Tiles = new List<Image>();

                for (var cur_x = 0; cur_x < maxRows + 1; cur_x++)
                {
                    for (var cur_y = 0; cur_y < maxCols + 1; cur_y++)
                    {
                        var fdid = tileDict[cur_x + "," + cur_y];

                        if (buildInstance.Root.FileExists((uint)fdid))
                        {
                            try
                            {
                                using (var stream = new MemoryStream(buildInstance.OpenFileByFDID((uint)fdid)))
                                {
                                    using var layer = LoadAndResizeBlp(stream);
                                    var layerPositioned = layer.Embed(
                                        x: cur_y * 256,
                                        y: cur_x * 256,
                                        width: image1.Width,
                                        height: image1.Height
                                    );
                                    image1Tiles.Add(layerPositioned);
                                }
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine("An error occured opening BLP with filedataid " + fdid + ": " +
                                                  e.Message);
                            }
                        }
                    }
                }

                if (saveUnexplored && image1 != null)
                {
                    SaveImage(image1, image1Tiles, Path.Join("unexplored", $"{cleanMapName}.png"));
                }

                if (!saveLayers && !saveExplored)
                {
                    continue;
                }

                // TODO: add alpha?
                using var image2 = NewTransparentImage((int)res_y, (int)res_x);
                var image2Tiles = new List<Image>();

                foreach (dynamic wmorow in WorldMapOverlay.Values)
                {
                    var WMOUIMapArtID = wmorow.UiMapArtID;
                    var offsetX = wmorow.OffsetX;
                    var offsetY = wmorow.OffsetY;

                    uint maxWMORows = 0;
                    uint maxWMOCols = 0;
                    var wmoTileDict = new Dictionary<string, int>();

                    if (WMOUIMapArtID == uiMapArtID)
                    {
                        foreach (dynamic wmotrow in WorldMapOverlayTile.Values)
                        {
                            var worldMapOverlayID = wmotrow.WorldMapOverlayID;

                            // something wrong in/around this check
                            if (worldMapOverlayID == wmorow.ID)
                            {
                                var fdid = wmotrow.FileDataID;
                                var rowIndex = wmotrow.RowIndex;
                                var colIndex = wmotrow.ColIndex;
                                var layerIndex = wmotrow.LayerIndex;

                                // Skip other layers for now
                                if (layerIndex != 0)
                                {
                                    continue;
                                }

                                if (rowIndex > maxWMORows)
                                {
                                    maxWMORows = rowIndex;
                                }

                                if (colIndex > maxWMOCols)
                                {
                                    maxWMOCols = colIndex;
                                }

                                wmoTileDict.Add(rowIndex + "," + colIndex, fdid);
                            }
                        }
                    }

                    if (wmoTileDict.Count == 0)
                    {
                        continue;
                    }

                    var layerResX = (maxWMORows + 1) * 256;
                    var layerResY = (maxWMOCols + 1) * 256;

                    using var layerImage = NewTransparentImage((int)res_y, (int)res_x);

                    for (var cur_x = 0; cur_x < maxWMORows + 1; cur_x++)
                    {
                        for (var cur_y = 0; cur_y < maxWMOCols + 1; cur_y++)
                        {
                            var fdid = wmoTileDict[cur_x + "," + cur_y];

                            if (!buildInstance.Root.FileExists((uint)fdid))
                            {
                                continue;
                            }

                            try
                            {
                                using (var stream = new MemoryStream(buildInstance.OpenFileByFDID((uint)fdid)))
                                {
                                    using var layer = LoadAndResizeBlp(stream);

                                    var posY = cur_y * 256 + offsetX;
                                    var posX = cur_x * 256 + offsetY;

                                    // if (saveLayers)
                                    // {
                                    //     layerImage.Mutate(ctx =>
                                    //         ctx.DrawImage(layer, new Point(cur_y * 256, cur_x * 256), Options));
                                    // }

                                    var layerPositioned = layer.Embed(
                                        x: posY,
                                        y: posX,
                                        width: image1.Width,
                                        height: image1.Height
                                    );
                                    image1Tiles.Add(layerPositioned);

                                    // if (saveExploredMapsWithoutUnexplored)
                                    // {
                                    //     image2.Mutate(ctx =>
                                    //         ctx.DrawImage(layer, new Point(posY, posX), Options));
                                    // }
                                }
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine("An error occured opening BLP with filedataid " + fdid);
                            }
                        }
                    }

                    // if (saveLayers)
                    // {
                    //     string layersPath = Path.Join("layers", cleanMapName);
                    //     if (!Directory.Exists(layersPath))
                    //     {
                    //         Directory.CreateDirectory(layersPath);
                    //     }
                    //
                    //     SaveImage(layerImage, Path.Join("layers", cleanMapName, $"{wmorow.ID}.png"));
                    // }
                }

                if (saveExplored)
                {
                    SaveImage(image1, image1Tiles, Path.Join("explored", $"{cleanMapName}.png"));
                }
                
                image1Tiles.ForEach(img => img.Dispose());

                // if (saveExploredMapsWithoutUnexplored)
                // {
                //     SaveImage(image2, Path.Join("exploredNoUnexplored", $"{cleanMapName}.png"));
                // }
            }
        }
    }

    private static string CleanFileName(string fileName)
    {
        return Path.GetInvalidFileNameChars()
            .Aggregate(fileName, (current, c) => current.Replace(c.ToString(), string.Empty));
    }

    private static Image NewTransparentImage(int width, int height)
    {
        // 1. Create a 1-pixel black base image (has 1 band by default)
        using var basePixel = Image.Black(1, 1);

        // 2. Add 3 more empty bands to make it a 4-band image (RGBA)
        // Then force the data type to 8-bit unsigned char (uchar) and set interpretation to sRGB
        using var transparentPixel = basePixel.Bandjoin(0, 0, 0)
            .Cast(Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);

        // Now you can resize it to your target dimensions if needed
        var finalImage = transparentPixel.Embed(
            0,
            0, 
            width,
            height, 
            extend: Enums.Extend.Background, 
            background: new double[] { 0, 0, 0, 0 }
        );
        
        return finalImage;
    }

    private static Image LoadAndResizeBlp(Stream stream)
    {
        using var blp = new BLPFile(stream);
        var bgraBytes = blp.GetPixels(0, out int width, out int height);

        // load into a NetVips image
        using var vipsBgra = Image.NewFromMemory(bgraBytes, width, height, bands: 4, format: Enums.BandFormat.Uchar);
        
        // swap channels from BGRA to RGBA (R=2, G=1, B=0, A=3)
        using var vipsRgba = vipsBgra[2].Bandjoin(vipsBgra[1], vipsBgra[0], vipsBgra[3]);

        if (vipsRgba.Width == 256)
        {
            return vipsRgba.Copy(interpretation: Enums.Interpretation.Srgb);
        }
        else
        {
            using var copied = vipsRgba.Copy(interpretation: Enums.Interpretation.Srgb); 
            return copied.Resize((double)256 / copied.Width);
        }
    }

    private static void SaveImage(Image image, List<Image> overlayImages, string filename)
    {
        var blendModes = Enumerable.Repeat(Enums.BlendMode.Over, overlayImages.Count).ToArray();
        using var result = image.Composite(overlayImages.ToArray(), blendModes);
        result.WriteToFile(filename, PngVOption);
    }
}
