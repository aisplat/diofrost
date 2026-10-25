
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NAudio.Wave;

internal static class Program
{
    private const string AppName = "Diofrost";
    private const string Version = "0.0.1";

    private static readonly WaveFormat OutputFormat =
        new WaveFormat(44100, 16, 2);

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            ShowHelp();
            return 0;
        }

        if (args.Contains("-h") || args.Contains("--help"))
        {
            ShowHelp();
            return 0;
        }

        if (args.Contains("-v") || args.Contains("--version"))
        {
            Console.WriteLine($"{AppName} {Version}");
            return 0;
        }

        if (!args[0].Equals("generate",
                StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"Unknown command: {args[0]}");
            Console.Error.WriteLine(
                "Run 'diofrost -h' for help.");
            return 1;
        }

        try
        {
            return Generate(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Diofrost: {ex.Message}");
            return 1;
        }
    }

    private static int Generate(string[] args)
    {
        string? character = null;
        string? output = null;
        string? text = null;
        ulong? seed = null;
        double cutoffSeconds = 0;

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException(
                    $"Missing value for '{arg}'.");
            }

            string value = args[++i];

            switch (arg)
            {
                case "-c":
                    character = value;
                    break;

                case "-s":
                    if (!ulong.TryParse(
                            value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out ulong parsedSeed))
                    {
                        throw new ArgumentException(
                            $"Invalid seed: '{value}'. " +
                            "Expected a non-negative 64-bit integer.");
                    }

                    seed = parsedSeed;
                    break;

                case "-t":
                    text = value;
                    break;

                case "-o":
                    output = value;
                    break;

                case "-l":
                    if (!double.TryParse(
                            value,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out cutoffSeconds) ||
                        double.IsNaN(cutoffSeconds) ||
                        double.IsInfinity(cutoffSeconds) ||
                        cutoffSeconds < 0)
                    {
                        throw new ArgumentException(
                            $"Invalid cutoff: '{value}'. " +
                            "Expected a non-negative number of seconds.");
                    }

                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown option: '{arg}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(character))
        {
            throw new ArgumentException(
                "A character is required. Use -c <character>.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(
                "Text is required. Use -t <text>.");
        }

        if (character == "." ||
            character == ".." ||
            character.IndexOfAny(
                Path.GetInvalidFileNameChars()) >= 0 ||
            character.Contains('/') ||
            character.Contains('\\') ||
            Path.IsPathRooted(character))
        {
            throw new ArgumentException(
                "The character must be a valid folder name.");
        }

        string baseDir = AppContext.BaseDirectory;
        string characterDir = Path.Combine(
            baseDir, "characters", character);

        if (!Directory.Exists(characterDir))
        {
            throw new DirectoryNotFoundException(
                $"Character directory not found: {characterDir}");
        }

        string[] clips = Directory
            .EnumerateFiles(
                characterDir, "*.wav",
                SearchOption.TopDirectoryOnly)
            .OrderBy(
                Path.GetFileName,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                Path.GetFileName,
                StringComparer.Ordinal)
            .ToArray();

        if (clips.Length == 0)
        {
            throw new InvalidOperationException(
                $"No WAV clips found in '{characterDir}'.");
        }

        ulong actualSeed = seed ?? GetRandomSeed();

        string outputPath = output == null
            ? Path.Combine(
                baseDir, $"{character}_{actualSeed}.wav")
            : Path.GetFullPath(
                Path.IsPathRooted(output)
                    ? output
                    : Path.Combine(baseDir, output));

        if (clips.Any(clip => string.Equals(
                Path.GetFullPath(clip),
                outputPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The output path cannot overwrite a source clip.");
        }

        // fisher–yates shuffle using a seeded random number generator
        var random = new SeededRandom(actualSeed);

        for (int i = clips.Length - 1; i > 0; i--)
        {
            int j = random.NextInt(i + 1);
            (clips[i], clips[j]) = (clips[j], clips[i]);
        }

        var characterMap =
            new System.Collections.Generic.Dictionary<char, string>();

        foreach (char c in text)
        {
            if (!characterMap.ContainsKey(c) && !char.IsWhiteSpace(c))
            {
                characterMap[c] = clips[random.NextInt(clips.Length)];
            }
        }

        Console.WriteLine($"{AppName} {Version}");
        Console.WriteLine($"Character: {character}");
        Console.WriteLine($"Seed:      {actualSeed}");
        Console.WriteLine($"Text:      {text}");
        Console.WriteLine($"Clips:     {text.Count(c => !char.IsWhiteSpace(c))}");
        Console.WriteLine($"Cutoff:    {cutoffSeconds} seconds");
        Console.WriteLine($"Output:    {outputPath}");
        Console.WriteLine();

        int completed = 0;
        int total = text.Count(c => !char.IsWhiteSpace(c));

        int lastCharacterIndex = -1;

        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                lastCharacterIndex = i;
                break;
            }
        }

        try
        {
            using (var writer = new WaveFileWriter(
                       outputPath, OutputFormat))
            {
                byte[] buffer = new byte[65536];

                foreach (char c in text)
                {
                    if (char.IsWhiteSpace(c))
                        continue;

                    string clip = characterMap[c];

                    Console.WriteLine(
                        $"[{completed + 1}/{total}] " +
                        $"'{c}' -> {Path.GetFileName(clip)}");

                    using var reader = new WaveFileReader(clip);

                    using WaveStream converted =
                        reader.WaveFormat.Equals(OutputFormat)
                            ? reader
                            : new WaveFormatConversionStream(OutputFormat, reader);

                    long bytesToWrite = converted.Length;

                    if (cutoffSeconds > 0)
                    {
                        int blockAlign = converted.WaveFormat.BlockAlign;

                        long bytesToTrim = (long)Math.Ceiling(
                            cutoffSeconds *
                            converted.WaveFormat.AverageBytesPerSecond);

                        bytesToTrim =
                            ((bytesToTrim + blockAlign - 1) / blockAlign)
                            * blockAlign;

                        bytesToWrite = Math.Max(
                            0, converted.Length - bytesToTrim);

                        bytesToWrite -= bytesToWrite % blockAlign;
                    }

                    int bytesRead;

                    while (bytesToWrite > 0 &&
                           (bytesRead = converted.Read(
                               buffer,
                               0,
                               (int)Math.Min(buffer.Length, bytesToWrite))) > 0)
                    {
                        writer.Write(buffer, 0, bytesRead);
                        bytesToWrite -= bytesRead;
                    }

                    completed++;
                }
            }
        }
        catch
        {
            try
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
            catch
            {
            }

            throw;
        }

        Console.WriteLine();
        Console.WriteLine("Dialogue generated successfully!");

        return 0;
    }

    private static ulong GetRandomSeed()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(8);
        return BitConverter.ToUInt64(bytes);
    }

    private static void ShowHelp()
    {
        Console.WriteLine($"""
            {AppName} {Version}
            Inkling / random voice byte dialogue generation

            Usage:
              diofrost generate -c <character> -t <text> [-s <seed>] [-o <output.wav>] [-l <seconds>]

            Commands:
              generate              Generate randomized dialogue

            Options:
              -h, --help            Show help
              -v, --version         Show version
              -c <character>        Character folder (required)
              -t <text>             Text to generate dialogue for (required)
              -s <seed>             Unsigned 64-bit random seed
              -o <output.wav>       Output path (optional)
              -l <seconds>          Trim seconds from the final voice clip (optional)

            Defaults:
              Character directory: ./characters/<character>/
              Output file:         ./<character>_<seed>.wav
              Seed:                Randomly generated
              Cutoff:              0 seconds

            Examples:
              diofrost generate -c aori -t "HELLO WORLD"
              diofrost generate -c aori -s 137265782872763 -t "HELLO WORLD"
              diofrost generate -c aori -s 137265782872763 -t "HELLO WORLD" -o aorispeech.wav
              diofrost generate -c aori -t "HELLO WORLD" -l 0.15
            """);
    }

    private sealed class SeededRandom
    {
        private ulong state;

        public SeededRandom(ulong seed)
        {
            state = seed;
        }

        private ulong NextUInt64()
        {
            state += 0x9E3779B97F4A7C15UL;

            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;

            return z ^ (z >> 31);
        }

        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxExclusive));

            return (int)(NextUInt64() % (uint)maxExclusive);
        }
    }
}
