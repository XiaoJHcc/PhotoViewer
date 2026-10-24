using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

internal static class OpenRequestChannelChecks
{
    public static async Task RunAsync(Checks c, string root)
    {
        c.Section("外部打开请求队列");
        var firstFile = new LocalFileRef(Path.Combine(root, "first.jpg"));
        var secondFile = new LocalFileRef(Path.Combine(root, "second.jpg"));
        var first = new OpenRequest([firstFile], OpenRequestSource.SystemOpen);
        var duplicate = new OpenRequest([firstFile], OpenRequestSource.Share);
        var second = new OpenRequest([secondFile], OpenRequestSource.SystemOpen);
        c.True("同目标不同来源视为重复", first.HasSameTargets(duplicate));
        c.True("不同目标不视为重复", !first.HasSameTargets(second));

        var channel = new OpenRequestChannel();
        c.True("UI 就绪前接受请求", channel.Publish(first));
        c.True("pending 重复请求被合并", !channel.Publish(duplicate));
        c.True("不同请求可入队", channel.Publish(second));
        c.True("空批次不入队", !channel.Publish(new OpenRequest([], OpenRequestSource.Automation)));

        var opened = new List<string>();
        var errors = new List<string>();
        var consumer = channel.RunAsync(async (request, token) =>
        {
            await Task.Yield();
            if (request.Items[0].Name == "first.jpg") throw new IOException("injected");
            opened.Add(request.Items[0].Name);
        }, (request, exception) => errors.Add(request.Items[0].Name + ":" + exception.Message));

        await Task.Delay(30);
        var secondConsumer = await Task.Run(async () =>
        {
            try
            {
                await channel.RunAsync((_, _) => Task.CompletedTask, (_, _) => { });
                return "no error";
            }
            catch (InvalidOperationException)
            {
                return "single consumer";
            }
        });
        c.Equal("只允许一个消费者", "single consumer", secondConsumer);

        channel.Complete();
        await consumer.WaitAsync(TimeSpan.FromSeconds(2));
        c.Equal("handler 异常后继续下一项", "second.jpg", string.Join(',', opened));
        c.Equal("异常有统一出口", "first.jpg:injected", string.Join(',', errors));
        c.True("完成后拒绝新请求", !channel.Publish(new OpenRequest([firstFile], OpenRequestSource.CommandLine)));

        var drain = new OpenRequestChannel();
        drain.Publish(first);
        drain.Complete();
        var drained = 0;
        await drain.RunAsync((_, _) =>
        {
            drained++;
            return Task.CompletedTask;
        }, (_, _) => { }).WaitAsync(TimeSpan.FromSeconds(2));
        c.Equal("完成前排空已接受请求", 1, drained);
    }
}
