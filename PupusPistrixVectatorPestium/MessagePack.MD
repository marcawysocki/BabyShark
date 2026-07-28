MessagePack.MD

I would like the Json file information that is currently in BaseManager.cs and I would like that to be in initialbasemanager but instead of Json it's going to be MessagePack.
I would also like a debug obtion to read and write the MessagePack data to json human readable
file for testing purposes.

Next we will work on getting the labels working properly.

using System;
using System.IO;
using MessagePack;

[MessagePackObject]
public class LabeledPoint
{
    [Key(0)]
    public string Label { get; set; }

    [Key(1)]
    public int unitType { get; set; }

    [Key(2)]
    public int unitIndex { get; set; }

    [Key(3)]
    public float X { get; set; }

    [Key(4)]
    public float Y { get; set; }

    [Key(5)]
    public string role { get; set; }
}

class MessagePackUnpack

{
    static void MessagePackerUnpacker()
    {
        var point = new LabeledPoint;
        
        var fileName = GetGeneratedBaseDataFileName(gameInfo.MapName, folder, gameInfo.StartRaw.StartLocations.First());
        string filePath =   fileName + ".labelPoint.dat";
        // Serialize to file
        byte[] data = MessagePackSerializer.Serialize(point);
        File.WriteAllBytes(filePath, data);

        // Deserialize from file
        byte[] loadedData = File.ReadAllBytes(filePath);
        LabeledPoint restored = MessagePackSerializer.Deserialize<LabeledPoint>(loadedData);

        Console.WriteLine($"Label: {restored.Label}, X: {restored.X}, Y: {restored.Y}");
    }
}
