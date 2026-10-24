# Разработка модулей

## C# DLL

Скопируйте `examples/hello` и подключите проект `Runpad.Contracts`. Из зависимостей исключите собственную копию Contracts (`Private=false`, `ExcludeAssets=runtime`): модуль и хост должны использовать одну сборку контрактов. Флаг `EnableDynamicLoading=true` копирует зависимости и создаёт `.deps.json`.

Реализуйте один публичный класс `IRunpadModule`:

```csharp
public sealed class MyModule : IRunpadModule
{
    public Task StartAsync(ModuleContext context, CancellationToken ct) => Task.CompletedTask;
    public Task<object?> InvokeAsync(string method, JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object?>(new { message = "Модуль работает" });
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

`StartAsync` получает папку ресурсов и отдельную папку данных. `InvokeAsync("snapshot", ...)` вызывается в цикле агента; верните небольшой JSON-совместимый объект. Другие методы вызываются через `runpad module invoke <id> <method> [json]`. Все операции должны учитывать отмену. В DLL-модулях вывод Console перенаправлен в stderr; используйте его только для диагностических сообщений без секретов.

Манифест:

```json
{
  "id": "hello",
  "name": "Мой модуль",
  "version": "1.0.0",
  "apiVersion": 1,
  "kind": "dotnet",
  "entry": "HelloModule.dll",
  "host": "user",
  "permissions": [],
  "dependencies": [],
  "autostart": true
}
```

```powershell
dotnet publish modern/examples/hello/HelloModule.csproj -c Release -o C:\user_data\hello
Copy-Item modern/examples/hello/module.json C:\user_data\hello
& 'C:\Program Files\RunpadPro\runpad.cmd' module install C:\user_data\hello
& 'C:\Program Files\RunpadPro\runpad.cmd' module invoke hello snapshot
```

Пакет должен содержать DLL, её зависимости, `.deps.json`, `module.json`, ресурсы. `module.ready` добавляется после копирования; CLI делает это сама. Версии — числовые `major.minor.patch`. При несовместимом API, отсутствующих/отключённых зависимостях или цикле запуск блокируется. Зависимости определяют допустимость запуска; обмен данными и строгий порядок запуска между модулями пока не предоставляются.

## Отдельный EXE на любом языке

Укажите `kind: executable` и `entry: MyModule.exe`. EXE должен включать нужную среду выполнения либо иметь её установленной. Обмен — по одной строке JSON через stdin/stdout; stderr используется для логов:

```json
{"id":"123","method":"snapshot","payload":null}
```

Ответ обязательно повторяет `id` и содержит `result` даже при ошибке:

```json
{"id":"123","ok":true,"result":{"value":42},"error":null}
```

Не печатайте в stdout приветствия и логи. Сразу сбрасывайте буфер вывода. Лимит строки — 1 МБ, таймаут ответа — 10 секунд. При EOF завершите процесс и остановите дочерние процессы. Папка данных передаётся через `RUNPAD_MODULE_DATA`. ModuleHost автоматически завершает DLL-модуль при EOF.

Каждый DLL-модуль запускается в отдельном ModuleHost. Отдельный `AssemblyLoadContext` разрешает его зависимости. Сбой/таймаут завершает процесс; повторный запуск производится после задержки 30 секунд. Логи: `%LOCALAPPDATA%\RunpadPro\logs\modules.log`.

## Доверие и права

Модуль выполняется со всеми правами текущего пользователя. `permissions` — метаданные, не механизм изоляции. Отдельный процесс не является песочницей безопасности. Пользовательские расширения не получают административный канал службы. Для повышенных операций служба имеет фиксированный API и допускает только администраторов/System.

Не используйте WindowsDesktop/WPF/WinForms в обычном DLL-модуле: ModuleHost предоставляет базовый .NET Runtime. Для зависимостей от других framework используйте собственный EXE с нужной средой. Ресурсы HTML не получают host objects или bridge к системным операциям. Новый произвольный HTML не регистрирует автоматически отдельную вкладку; текущая точка расширения UI — HTML-фон и данные карточки модуля.
