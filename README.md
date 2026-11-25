# 🎵 uAudio

> WPF-приложение для редактирования и микширования аудио

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=.net)
![License](https://img.shields.io/badge/License-Apache%202.0-green?style=flat-square)
![Platform](https://img.shields.io/badge/Platform-Windows-blue?style=flat-square)

## 📖 Описание

**uAudio** — это аудиоредактор на базе WPF (.NET 8), позволяющий:
- 🎼 Работать с несколькими аудиодорожками
- ⏱️ Точно позиционировать клипы на временной шкале
- 🎚️ Регулировать громкость и включать/выключать дорожки
- 💾 Сохранять и загружать проекты
- 📤 Экспортировать микс в WAV-формат
- 🔄 Отменять действия
- 🔧 Автоматическое восстановление проекта

## 🚀 Быстрый старт

### Требования

- Windows 10/11
- .NET 8.0 SDK
- Visual Studio 2022 или VS Code с расширением C#

### Установка и запуск

```bash
# Клонирование репозитория
git clone https://github.com/AnfieldCorsair/uAudio.git
cd uAudio

# Восстановление зависимостей
dotnet restore

# Запуск
dotnet run --project src/uAudio/uAudio.csproj
```

### Сборка

```bash
# Debug
dotnet build

# Release
dotnet build -c Release

# Tests
dotnet test uAudio.sln
```


## 🎮 Использование

### Работа с дорожками

| Действие | Описание |
|----------|----------|
| Добавить дорожку | Нажмите кнопку "Add Track" |
| Удалить дорожку | Нажмите кнопку "X" на дорожке |
| Изменить громкость | Используйте слайдер Volume |
| Вкл/выкл звук | Поставьте/снимите галочку Mute |

### Работа с клипами

| Действие | Описание |
|----------|----------|
| Добавить клип(ы) | Нажмите `Import Audio` (`Ctrl+I`), `Open` (`Ctrl+O`), `+ Add File(s)` на дорожке или перетащите файлы/папки из Проводника |
| Drag & Drop из Проводника | Перетащите один или несколько аудиофайлов прямо в окно или на дорожку — появится выбор целевой дорожки или создания отдельной дорожки под каждый звук |
| Переместить клип | Перетащите клип мышкой |
| Cut/Trim | Выделите диапазон правой кнопкой мыши или используйте Ctrl+X / Ctrl+Shift+X |


### Горячие клавиши

Мини-памятка есть в приложении по `F1` и в [`docs/HOTKEYS.md`](docs/HOTKEYS.md).

| Комбинация | Действие |
|------------|----------|
| Ctrl+N / Ctrl+O | Новый проект / открыть проект или аудио |
| Ctrl+I | Импортировать аудиофайлы |
| Ctrl+S / Ctrl+Shift+S | Сохранить / сохранить как |
| Ctrl+E | Экспорт WAV |
| Ctrl+Z | Отмена |
| Ctrl+T | Добавить дорожку |
| Space / Esc | Play/Pause / стоп |
| Ctrl+X | Вырезать выделение |
| Ctrl+Shift+X | Обрезать вне выделения |
| Ctrl++ / Ctrl+- / Ctrl+0 | Зум |
| F1 | Памятка |

### Поддерживаемые форматы

```
Аудио файлы:
├── .wav  — Waveform Audio File Format (PCM 8/16/24/32-bit, IEEE Float 32/64-bit, Extensible, A-Law/Mu-Law)
├── .mp3  — MPEG Audio Layer III (Media Foundation + NLayer managed decoder)
├── .ogg / .oga — Ogg Vorbis
├── .flac — Free Lossless Audio Codec
├── .aiff / .aif — Audio Interchange File Format
├── .aac / .m4a / .mp4 — Advanced Audio Coding / MPEG-4 Audio
├── .opus / .webm — Opus Audio
└── .wma / .asf — Windows Media Audio
```

### Формат проекта

Проекты сохраняются в формате `.uaudioproj` (JSON):

```json
{
  "formatVersion": 1,
  "appName": "uAudio",
  "pixelsPerSecond": 100,
  "tracks": [
    {
      "trackName": "Drums",
      "volume": 0.8,
      "isMuted": false,
      "clips": [
        {
          "id": "...",
          "filePath": "audio/drums.wav",
          "isRelativePath": true,
          "startTime": 0,
          "offsetInFile": 0,
          "duration": 30
        }
      ]
    }
  ]
}
```

## ⚙️ Архитектура

### Потоковая архитектура аудио

```
┌─────────────┐     ┌──────────────────┐     ┌─────────────┐
│ Audio Files │────▶│ FilePlaybackContext│────▶│    Mixer    │
└─────────────┘     └──────────────────┘     └─────────────┘
       │                    │                        │
       │            ┌───────┴───────┐                │
       │            │  Sample Rate  │                │
       │            │  Conversion   │                │
       │            └───────────────┘                │
       │                    │                        │
       │            ┌───────┴───────┐                │
       │            │  Channel Map  │                │
       │            └───────────────┘                │
       └───────────────────────────┘──────────────────┘
```

### Система восстановления

```
┌─────────────────┐    Автосохранение    ┌─────────────────────┐
│                 │      (каждые 30с)    │                     │
│  Работа с      │──────────────────────▶│  Recovery File      │
│  проектом      │                       │  (last-recovery)    │
│                 │                       │                     │
└─────────────────┘                       └─────────────────────┘
                                                    │
                              ┌───────────────────────┘
                              ▼
                     ┌─────────────────┐
                     │ При запуске:   │
                     │                │
                     │ 1. Проверка    │
                     │    recovery    │
                     │                │
                     │ 2. Если есть:  │
                     │    предложить  │
                     │    восстанов. │
                     │                │
                     └─────────────────┘
```

## 🔧 Технические детали

### Зависимости

| Пакет | Версия | Назначение |
|-------|--------|------------|
| NAudio | 2.2.1 | Аудио-библиотека |
| NAudio.WinMM | 2.2.1 | DirectSound playback |
| System.Text.Json | (встроен) | Сериализация проектов |




### В планах:

- [ ] Неразрушающее редактирование
- [ ] Эффекты (реверберация, эквалайзер)
- [ ] Поддержка MIDI
- [ ] Плагины VST
- [ ] Темы оформления
- [x] Горячие клавиши
- [ ] Экспорт в MP3/FLAC


### Как внести вклад:

1. Fork репозитория
2. Создайте ветку для фичи (`git checkout -b feature/amazing-feature`)
3. Коммитьте изменения (`git commit -m 'Add amazing feature'`)
4. Push в ветку (`git push origin feature/amazing-feature`)
5. Откройте Pull Request

## 📝 Лицензия

Проект распространяется под лицензией Apache 2.0. Подробности в файле [LICENSE](LICENSE).

## 🙏 Благодарности

- [NAudio](https://github.com/naudio/NAudio) — аудио-библиотека для .NET
- Сообщество WPF за вдохновение и лучшие практики

---

**uAudio** — аудиоредактор для тех, кто ценит простоту и функциональность.
