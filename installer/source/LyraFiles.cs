using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal interface IModFiles
{
    byte[] Read(string path);
    void MakeDirectory(string path);
    void Write(string path, byte[] data);
    void Move(string from, string to);
}

// File operations used by Project Lyra 1.4.0's authoring tools.
// Wire layout is from deploy.py and the PULL_FILE method in zune_repl.py.
internal sealed class LyraFiles : IModFiles
{
    private readonly IPAddress address;
    private readonly int port;
    private const int MaxFileBytes = 131072;
    private readonly int maxReadBytes;

    public LyraFiles(string ip, int portNumber, int readLimit = 131072)
    {
        address = IPAddress.Parse(ip);
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("请输入 Zune 的 IPv4 地址。");
        port = portNumber;
        if (readLimit < 131072 || readLimit > 524288) throw new ArgumentOutOfRangeException("readLimit");
        maxReadBytes = readLimit;
    }

    private TcpClient Connect()
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            var pending = client.BeginConnect(address, port, null, null);
            using (pending.AsyncWaitHandle)
            {
                if (!pending.AsyncWaitHandle.WaitOne(5000))
                    throw new IOException("连接超时，请确认播放器保持 Wi-Fi 连接且屏幕亮着。");
                client.EndConnect(pending);
            }
            client.ReceiveTimeout = 8000;
            client.SendTimeout = 8000;
            client.NoDelay = true;
            if (Encoding.ASCII.GetString(Receive(client.GetStream(), 6)) != "Hello\n")
                throw new IOException("目标没有返回 Lyra 的连接标识。");
            return client;
        }
        catch { client.Close(); throw; }
    }

    private static byte[] Receive(Stream stream, int length)
    {
        var data = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int count = stream.Read(data, offset, length - offset);
            if (count == 0) throw new EndOfStreamException("播放器在传输中断开了连接。");
            offset += count;
        }
        return data;
    }

    private static void Number(byte[] target, int offset, uint value)
    {
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, target, offset, 4);
    }

    private static void Send(Stream stream, byte[] data)
    {
        stream.Write(data, 0, data.Length);
    }

    private static byte[] Header(byte opcode)
    {
        var data = new byte[32];
        data[0] = opcode;
        return data;
    }

    private static byte[] Response(Stream stream, byte opcode)
    {
        byte[] data = Receive(stream, 32);
        if (data[0] != opcode) throw new IOException("播放器的回复类型与请求不同。");
        return data;
    }

    private static byte[] PathBytes(string path)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(path);
        if (bytes.Length == 0 || bytes.Length > 256 || path.Contains("..") || path.Contains("\0"))
            throw new ArgumentException("设备路径无效。");
        return bytes;
    }

    // Retrying this request is read-only. A chunk is appended only after a complete response.
    private byte[] ReadChunk(byte[] encoded, int offset, int chunk)
    {
        byte[] args = new byte[16 + encoded.Length];
        Number(args, 0, 15);
        Number(args, 4, (uint)offset);
        Number(args, 8, (uint)chunk);
        Number(args, 12, (uint)encoded.Length);
        Buffer.BlockCopy(encoded, 0, args, 16, encoded.Length);
        using (TcpClient client = Connect())
        {
            NetworkStream stream = client.GetStream();
            byte[] header = Header(40);
            Number(header, 1, (uint)args.Length);
            Send(stream, header); Send(stream, args);
            byte[] reply = Response(stream, 40);
            uint length = BitConverter.ToUInt32(reply, 5);
            if (length < 4 || length > 8192 || BitConverter.ToInt32(reply, 1) != 0 || BitConverter.ToUInt32(reply, 9) != 0)
                throw new IOException("Lyra 文件读取服务返回异常。");
            return Receive(stream, (int)length);
        }
    }

    public byte[] Read(string path)
    {
        PathBytes(path);
        byte[] encoded = Encoding.Unicode.GetBytes(path);
        using (var output = new MemoryStream())
        {
            for (;;)
            {
                const int chunk = 7936;
                byte[] result;
                for (int attempt = 0; ; ++attempt)
                {
                    try { result = ReadChunk(encoded, (int)output.Length, chunk); break; }
                    catch (IOException) { if (attempt >= 1) throw; System.Threading.Thread.Sleep(250); }
                    catch (SocketException) { if (attempt >= 1) throw; System.Threading.Thread.Sleep(250); }
                }
                uint status = BitConverter.ToUInt32(result, 0);
                if (output.Length == 0 && (status == 0xE1000002 || status == 0xE1000003)) return null;
                if (status != 0) throw new IOException(String.Format("读取失败，设备错误 0x{0:X8}。", status));
                int count = result.Length - 4;
                if (count > chunk || output.Length + count > maxReadBytes) throw new IOException("文件长度超出此试用安装器的范围。");
                output.Write(result, 4, count);
                if (count < chunk) return output.ToArray();
            }
        }
    }

    public void MakeDirectory(string path)
    {
        byte[] name = PathBytes(path);
        using (TcpClient client = Connect())
        {
            NetworkStream stream = client.GetStream();
            byte[] request = Header(13);
            Number(request, 1, (uint)name.Length);
            Send(stream, request);
            Send(stream, name);
            byte[] reply = Response(stream, 13);
            uint error = BitConverter.ToUInt32(reply, 1);
            if (reply[5] == 0 && error != 183)
                throw new IOException(String.Format("建立插件目录失败，设备错误 {0}。", error));
        }
    }

    public void Write(string path, byte[] data)
    {
        byte[] name = PathBytes(path);
        if (data.Length == 0 || data.Length > MaxFileBytes) throw new ArgumentException("文件长度无效。");
        using (TcpClient client = Connect())
        {
            NetworkStream stream = client.GetStream();
            int offset = 0;
            while (offset < data.Length)
            {
                int count = Math.Min(16384, data.Length - offset);
                byte[] request = Header(12);
                Number(request, 1, (uint)name.Length);
                Number(request, 5, (uint)offset);
                Number(request, 9, (uint)count);
                Send(stream, request);
                Send(stream, name);
                stream.Write(data, offset, count);
                byte[] reply = Response(stream, 12);
                uint written = BitConverter.ToUInt32(reply, 1);
                if (reply[9] == 0 || written != count)
                    throw new IOException(String.Format("文件传输未完成，设备错误 {0}。可重新运行安装。", BitConverter.ToUInt32(reply, 5)));
                offset += count;
            }
        }
    }

    public void Move(string from, string to)
    {
        byte[] source = PathBytes(from), target = PathBytes(to);
        using (TcpClient client = Connect())
        {
            NetworkStream stream = client.GetStream();
            byte[] request = Header(17);
            Number(request, 1, (uint)source.Length);
            Number(request, 5, (uint)target.Length);
            Send(stream, request);
            Send(stream, source);
            Send(stream, target);
            byte[] reply = Response(stream, 17);
            if (reply[5] == 0)
                throw new IOException(String.Format("提交文件失败，设备错误 {0}。可重新检查安装状态。", BitConverter.ToUInt32(reply, 1)));
        }
    }
}
