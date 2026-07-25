using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Niratan.Models.Nyaa;

namespace Niratan.Services.Nyaa;

public interface INyaaDownloadManager
{
    event EventHandler? TasksChanged;

    IReadOnlyList<NyaaDownloadTaskSnapshot> GetTasks();

    string Enqueue(NyaaTorrentItem item);

    Task PauseAsync(string taskId);

    Task ResumeAsync(string taskId);

    void Cancel(string taskId);

    void Retry(string taskId);

    void Remove(string taskId);
}
