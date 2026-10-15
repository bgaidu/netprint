using System;
using System.Threading;

namespace PrintServer
{
    /// <summary>
    /// 健康看门狗：定期检查 Web 服务是否健康，异常时自动重启服务。
    /// 每 15 秒检测一次，连续失败 5 次（约75秒）触发重启，最多尝试 3 次。
    /// </summary>
    internal sealed class HealthWatchdog : IDisposable
    {
        private readonly Timer _timer;
        private int _failCount = 0;
        private int _restartCount = 0;
        private readonly int _maxRestarts = 3;

        public HealthWatchdog()
        {
            _timer = new Timer(Check, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            _timer.Change(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            Log.Info("健康看门狗已启动 (15秒检测间隔)");
        }

        private void Check(object state)
        {
            try
            {
                var server = Program.Server;
                if (server == null) return;

                if (server.IsHealthy)
                {
                    _failCount = 0;
                    return;
                }

                _failCount++;
                Log.Warn($"健康检查异常 (连续 {_failCount}/5): Web 服务不健康");

                if (_failCount >= 5)
                {
                    if (_restartCount >= _maxRestarts)
                    {
                        Log.Error($"健康看门狗连续重启 {_maxRestarts} 次失败，停止自动恢复");
                        _timer.Change(Timeout.Infinite, Timeout.Infinite); // 停止
                        return;
                    }
                    _failCount = 0;
                    _restartCount++;
                    Log.Warn($"健康看门狗尝试自动重启服务 ({_restartCount}/{_maxRestarts})...");
                    try
                    {
                        Program.RestartServer();
                        Log.Info("健康看门狗：服务已重启");
                    }
                    catch (Exception ex)
                    {
                        Log.Error("健康看门狗重启失败: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("健康检查异常: " + ex.Message);
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}