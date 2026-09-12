// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/net/Sink.h and net/ByteArraySink.h.
namespace FlintLauncher.Core.Net;

/// <summary>
/// Destination of a NetRequest's response body. Ports the C++ Task::State-returning methods as
/// bools (Running->true, Failed->false; Succeeded is expressed by the request itself).
/// </summary>
public abstract class Sink
{
    private readonly List<Validator> _validators = [];

    public string FailReason { get; protected set; } = "";

    public void AddValidator(Validator validator)
    {
        if (validator is not null)
        {
            _validators.Add(validator);
        }
    }

    public abstract bool Init(NetRequestSpec request);

    public abstract bool Write(byte[] data);

    public abstract bool Abort();

    public abstract bool Finalize(NetResponseInfo response);

    /// <summary>True when a usable local copy exists (used to decide 304 handling).</summary>
    public abstract bool HasLocalData();

    protected bool InitAllValidators(NetRequestSpec request)
    {
        foreach (var validator in _validators)
        {
            if (!validator.Init(request))
            {
                return false;
            }
        }
        return true;
    }

    protected bool FinalizeAllValidators(NetResponseInfo response)
    {
        foreach (var validator in _validators)
        {
            if (!validator.Validate(response))
            {
                return false;
            }
        }
        return true;
    }

    protected bool FailAllValidators()
    {
        var success = true;
        foreach (var validator in _validators)
        {
            success &= validator.Abort();
        }
        return success;
    }

    protected bool WriteAllValidators(byte[] data)
    {
        foreach (var validator in _validators)
        {
            if (!validator.Write(data))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>Port of net/ByteArraySink.h: downloads into an in-memory buffer.</summary>
public sealed class ByteArraySink : Sink
{
    private readonly string? _name;
    private readonly System.IO.MemoryStream _stream = new();

    public ByteArraySink(string? name = null)
    {
        _name = name;
    }

    public byte[] Data => _stream.ToArray();

    public string DataAsString => System.Text.Encoding.UTF8.GetString(_stream.ToArray());

    public override bool Init(NetRequestSpec request)
    {
        _stream.SetLength(0);
        return InitAllValidators(request);
    }

    public override bool Write(byte[] data)
    {
        if (!WriteAllValidators(data))
        {
            FailReason = "Failed writing into " + (_name ?? "byte array") + ": validators failed";
            return false;
        }
        _stream.Write(data);
        return true;
    }

    public override bool Abort()
    {
        _stream.SetLength(0);
        return FailAllValidators();
    }

    public override bool Finalize(NetResponseInfo response) => FinalizeAllValidators(response);

    public override bool HasLocalData() => _stream.Length > 0;
}
