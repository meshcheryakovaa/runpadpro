# Runpad Pro

В репозитории находятся исходная система Runpad Pro и новая модульная версия для **Windows 10/11 x64**.

**Новая версия:** [modern/README.md](modern/README.md) · [архитектура](modern/docs/architecture.md) · [разработка модулей](modern/docs/modules.md).

Новая реализация использует C#/.NET 10, WPF, WebView2 и отдельные процессы расширений. GitHub Actions собирает установщик, portable ZIP, сервер и пакеты модулей. Предварительные сборки публикуются в [Releases](../../releases).

Это новая реализация с собственным API v1. Совместимость со старыми EXE, COM API, DLL-плагинами, конфигурацией и протоколом исходной версии не заявлена. Исходные каталоги `admin`, `client`, `common`, `operator`, `server`, `tools` сохранены.
