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
- 🔄 Отменять и повторять действия
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
| Добавить клип | Нажмите "+" на дорожке и выберите аудиофайл |
| Переместить клип | Перетащите клип мышкой |
| Изменить границы | Перетащите края клипа |


### Поддерживаемые форматы

```
Аудио файлы:
├── .wav  — Waveform Audio File Format
├── .mp3  — MPEG Audio Layer III
├── .aiff — Audio Interchange File Format
├── .aac  — Advanced Audio Coding
├── .m4a  — MPEG-4 Audio
├── .flac — Free Lossless Audio Codec
└── .wma  — Windows Media Audio
```

### Формат проекта

Проекты сохраняются в формате `.uaudioproj` (JSON):

```json
{
  "formatVersion": 1,
  "appName": "uAudio Editor Pro",
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
- [ ] Горячие клавиши
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
