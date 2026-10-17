// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/net/NetJob.{h,cpp}: a ConcurrentTask of NetRequests sharing one HttpClient.

using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Net;

public class NetJob : ConcurrentTask
{
    public NetJob(string name = "", int maxConcurrent = DefaultMaxConcurrent)
        : base(name, maxConcurrent)
    {
    }

    public void AddNetRequest(NetRequest request) => AddTask(request);

    public IReadOnlyList<NetRequest> Requests => Queue.OfType<NetRequest>().Concat(Done.Keys.OfType<NetRequest>()).ToList();
}
