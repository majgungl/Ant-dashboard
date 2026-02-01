using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AntDashboard
{
    internal static class Program
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        private static void Main()
        {
            var manager = new AntManager(5);
            manager.Start();

            using (var cts = new CancellationTokenSource())
            {
                var dashboardServer = new DashboardServer(manager, cts.Token);
                dashboardServer.Start();

                Console.WriteLine("ANT dashboard running.");
                Console.WriteLine("Open http://localhost:3000 in a browser.");
                Console.WriteLine("Press R to reset pairing, Ctrl+C to exit.");

                Console.CancelKeyPress += (sender, args) =>
                {
                    args.Cancel = true;
                    cts.Cancel();
                };

                while (!cts.IsCancellationRequested)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (key.Key == ConsoleKey.R)
                        {
                            manager.ResetPairing();
                        }
                    }

                    Thread.Sleep(50);
                }
            }
        }

        private sealed class DashboardServer
        {
            private readonly AntManager _manager;
            private readonly CancellationToken _token;
            private readonly HttpListener _httpListener;
            private readonly HttpListener _wsListener;
            private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new ConcurrentDictionary<Guid, WebSocket>();

            public DashboardServer(AntManager manager, CancellationToken token)
            {
                _manager = manager;
                _token = token;
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add("http://localhost:3000/");

                _wsListener = new HttpListener();
                _wsListener.Prefixes.Add("http://localhost:3001/");
            }

            public void Start()
            {
                _httpListener.Start();
                _wsListener.Start();

                Task.Run(HandleHttpAsync, _token);
                Task.Run(HandleWebSocketsAsync, _token);
                Task.Run(BroadcastLoopAsync, _token);
            }

            private async Task HandleHttpAsync()
            {
                while (!_token.IsCancellationRequested)
                {
                    HttpListenerContext context = null;
                    try
                    {
                        context = await _httpListener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }

                    if (context == null)
                    {
                        continue;
                    }

                    if (context.Request.Url == null)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        continue;
                    }

                    if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = 405;
                        context.Response.Close();
                        continue;
                    }

                    var dashboardPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dashboard.html");
                    if (!File.Exists(dashboardPath))
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        continue;
                    }

                    var html = File.ReadAllText(dashboardPath);
                    var buffer = Encoding.UTF8.GetBytes(html);
                    context.Response.ContentType = "text/html";
                    context.Response.ContentLength64 = buffer.Length;
                    await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    context.Response.Close();
                }
            }

            private async Task HandleWebSocketsAsync()
            {
                while (!_token.IsCancellationRequested)
                {
                    HttpListenerContext context = null;
                    try
                    {
                        context = await _wsListener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }

                    if (context == null)
                    {
                        continue;
                    }

                    if (!context.Request.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        continue;
                    }

                    var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                    var clientId = Guid.NewGuid();
                    _clients[clientId] = wsContext.WebSocket;

                    _ = Task.Run(() => ListenForCloseAsync(clientId, wsContext.WebSocket), _token);
                }
            }

            private async Task ListenForCloseAsync(Guid clientId, WebSocket socket)
            {
                var buffer = new byte[256];
                try
                {
                    while (socket.State == WebSocketState.Open && !_token.IsCancellationRequested)
                    {
                        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }
                    }
                }
                catch (WebSocketException)
                {
                    // Ignore
                }
                finally
                {
                    _clients.TryRemove(clientId, out _);
                    try
                    {
                        if (socket.State == WebSocketState.Open)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (WebSocketException)
                    {
                        // Ignore
                    }
                }
            }

            private async Task BroadcastLoopAsync()
            {
                while (!_token.IsCancellationRequested)
                {
                    var riders = _manager.GetSnapshot();
                    var payload = riders.Select(r => new
                    {
                        rider = r.RiderNumber,
                        hr = r.HeartRate,
                        power = r.Power,
                        cadence = r.Cadence,
                        lastSeenMs = r.LastSeenMs(),
                        hrDevice = r.HrDeviceNumber,
                        pwrDevice = r.PowerDeviceNumber
                    }).ToArray();

                    var json = Serializer.Serialize(payload);
                    var data = Encoding.UTF8.GetBytes(json);
                    var segment = new ArraySegment<byte>(data);

                    foreach (var client in _clients.ToArray())
                    {
                        if (client.Value.State != WebSocketState.Open)
                        {
                            _clients.TryRemove(client.Key, out _);
                            continue;
                        }

                        try
                        {
                            await client.Value.SendAsync(segment, WebSocketMessageType.Text, true, _token).ConfigureAwait(false);
                        }
                        catch (WebSocketException)
                        {
                            _clients.TryRemove(client.Key, out _);
                        }
                    }

                    await Task.Delay(150, _token).ConfigureAwait(false);
                }
            }
        }
    }
}
