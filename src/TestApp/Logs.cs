﻿namespace TestApp;

internal static class Logs
{
    public static void Clear()
    {
        while (FetchNext() != null) ;
    }

    public static IEnumerable<string> Fetch()
    {
        while (true)
        {
            var log = FetchNext();

            if (log == null)
            {
                yield break;
            }

            if (log.StartsWith("Error:"))
            {
                throw new Exception($"Found error log: {log}");
            }

            yield return log;
        }
    }

    public static void AssertContains(List<string> logs, string expected)
    {
        if (!logs.Contains(expected))
        {
            Console.WriteLine($"Could not find log: '{expected}'");

            Fail(logs);
        }
    }

    public static void Assert(bool value)
    {
        if (!value)
        {
            Fail();
        }
    }

    private static void Fail(IEnumerable<string>? logs = null)
    {
        Console.WriteLine("********* Assertion failed, dumping logs *********");

        logs ??= Fetch();

        foreach (var log in logs)
        {
            Console.WriteLine(log);
        }

        throw new Exception("Assertion failed");
    }

    private static unsafe string? FetchNext()
    {
        const int bufferSize = 1024;
        Span<char> buffer = stackalloc char[bufferSize];

        fixed (char* c = buffer)
        {
            int length = PInvokes.FetchLastLog(c, buffer.Length);

            return length >= 0 ? new string(buffer.Slice(0, length)) : null;
        }
    }
}