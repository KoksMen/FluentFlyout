<p align="center">
  <picture>
    <source width="70%" alt="FluentFlyout Title" media="(prefers-color-scheme: dark)" srcset="https://github.com/user-attachments/assets/daa2969f-8ad2-4832-8253-26133a50c921" />
    <source width="70%" alt="FluentFlyout Title" media="(prefers-color-scheme: light)" srcset="https://github.com/user-attachments/assets/3e75e514-b9a6-40e3-b170-af9d29a82bb4" />
    <img width="70%" alt="FluentFlyout Title" src="https://github.com/user-attachments/assets/daa2969f-8ad2-4832-8253-26133a50c921">
  </picture>
</p>

<h1 align="center">✨ FluentFlyout (KoksMen Edition) ✨</h1>
<h3 align="center">🚀 Версия форка: <em>v2.15.0-f2.3.0</em> 🪄</h3>

<p align="center">
  <b>Современные плавные оверлеи Windows 11 с оптимизацией для игр, бесшовным автообновлением в приложении, историей буфера обмена, регулировкой звука колесиком мыши и поддержкой любых GPU</b><br>
  <i>Modern fluent flyouts for Windows 11 with gaming safeguards, in-app seamless auto-updater, clipboard history, mouse wheel volume control, and universal GPU support</i>
</p>

<p align="center">
  <a href="https://github.com/KoksMen/FluentFlyout/releases"><img src="https://img.shields.io/badge/Version-v2.15.0--f2.3.0-7c4dff?style=for-the-badge&logo=github&logoColor=white" alt="FluentFlyout v2.15.0-f2.3.0" /></a>
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10.0" />
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x64)-0078D6?style=for-the-badge&logo=windows&logoColor=white" alt="Windows 10/11" />
  <img src="https://img.shields.io/badge/Size-~84%20MB-success?style=for-the-badge&logo=files&logoColor=white" alt="Compressed Size" />
  <img src="https://img.shields.io/badge/Signature-Authenticode%20SHA--256-success?style=for-the-badge&logo=windows&logoColor=white" alt="Authenticode Signed" />
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-GPL--3.0-orange?style=for-the-badge" alt="License GPL-3.0" /></a>
</p>

<p align="center">
  <a href="#-fluentflyout-на-русском"><b>🇷🇺 Перейти к русской версии</b></a> •
  <a href="#-fluentflyout-in-english"><b>🇬🇧 Switch to English Version</b></a>
</p>

<p align="center">
  <a href="https://github.com/KoksMen/FluentFlyout/releases/latest">
    <img src="https://img.shields.io/badge/⬇️%20Скачать%20релиз-FluentFlyout%20v2.15.0--f2.1.1-blue?style=for-the-badge" alt="Download Release" />
  </a>
</p>

---

# 🇷🇺 FluentFlyout (На русском)

## 💡 О проекте

**FluentFlyout (KoksMen Edition)** — это расширенный и глубоко оптимизированный форк приложения FluentFlyout для Windows 10 и 11.

Оригинальное приложение предоставляет красивые оверлеи в стиле **Fluent 2 Design**, однако страдало от ряда ограничений: свёртывание полноэкранных игр, задержки UI Automation в таскбаре, отсутствие встроенного скачивания обновлений, артефакты отображения на встроенной графике (iGPU), потеря флайаута громкости и привязка к закрытым сервисам телеметрии.

Этот форк полностью устраняет указанные проблемы, снижает размер дистрибутива более чем в 2 раза и делает приложение максимально стабильным, автономным и удобным для повседневной работы и гейминга! 🚀

---

## 🌟 Ключевые возможности и улучшения форка

### 🔄 1. Бесшовное автообновление прямо в приложении (как в Portal-Windows)
- **Прямая интеграция с GitHub Releases:** Приложение скачивает обновление напрямую из релизов репозитория `KoksMen/FluentFlyout` с наглядным отображением прогресса в процентах прямо в интерфейсе настроек.
- **Автоматическая установка:** Скачанный дистрибутив распаковывается, скрипт завершает работу старого процесса по PID, перезаписывает файлы и автоматически перезапускает обновленный `FluentFlyout.exe`. Браузер открывать не требуется!
- **Интеллектуальный SemVer:** Парсер `ForkVersion` корректно сопоставляет составные версии форка (`2.15.0-f2.3.0` > `2.15.0-f2.1.0` > `2.15.0-f2.0.0` > `2.13.1-f1.7.0`).

### 🔊 2. Надежный микшер громкости и подавление системного Windows OSD
- **Реакция на любые источники звука:** Подписка на низкоуровневые события аудиоэндпоинта (`AudioEndpointVolume.OnVolumeNotification`). Флайаут мгновенно появляется при смене звука с любых устройств: клавиатурных Fn-клавиш ноутбуков, колесиков мультимедийных клавиатур, внешних звуковых карт (ЦАП), мыши и сторонних программ.
- **Полное подавление стандартного OSD Windows:** Устранена ошибка, восстанавливавшая системный индикатор громкости при каждом закрытии флайаута. Поддерживаются оверлеи Windows 11 (`XamlExplorerHostIslandWindow`) и Windows 10 (`NativeHWNDHost` / `DirectUIHWND`).

### 🎮 3. Раздельные режимы полноэкранного экрана (Gaming Safeguard)
- **Exclusive Fullscreen:** Независимый переключатель для 3D-игр. При его отключении оверлеи не перехватывают фокус и не сворачивают полноэкранные игры.
- **Borderless Fullscreen:** Отдельная настройка для игр в окне без рамок и просмотра видео.
- **Скрытие индикатора Caps Lock в играх:** Предотвращает случайное сворачивание игр при нажатии клавиши Caps Lock.

### ⚡ 4. Молниеносный отклик кнопок таскбара (Win32 Fast Path)
- **Мгновенное открытие:** Устранены синхронные блокировки UI-потока. Позиция элементов таскбара рассчитывается моментально через нативный Win32 `GetWindowRect`.
- **Надежность повторного клика:** Устранены залипания кнопок микшера и буфера обмена.

### 🔊 5. Регулировка громкости колесиком мыши (Volume Wheel Scroll)
- **Прокрутка над кнопкой микшера:** Вращение колесика мыши над иконкой микшера в таскбаре плавно меняет общую громкость системы (настраиваемый шаг 1–10%, по умолчанию 2%).
- **Прокрутка над медиа-виджетом:** Прокрутка над мини-виджетом трека также меняет громкость с красивым оверлеем Flyout OSD.

### 🎯 6. Фокусировка и переход на вкладку браузера / окно плеера (Tab Activation)
- При клике на название трека или обложку флайаут активирует соответствующую вкладку в браузере (Edge, Chrome, Firefox, Opera, Brave, Vivaldi) через UI Automation или мгновенно выводит окно плеера (Spotify, Yandex Music, AIMP) на передний план.

### 📐 7. Поддержка компактной / маленькой панели задач Windows 11
- Автоматическая адаптация виджета и кнопок быстрых действий под уменьшенные размеры панели задач (StartAllBack, Windhawk, кастомные размеры 28–36px) без обрезания элементов.

### 🖱️ 8. Контекстное меню по правому клику на таскбар-виджет
- Быстрое меню: управление треками, переключение между активными плеерами в один клик и быстрый переход в настройки. Возможность отключения меню в параметрах.

### 📋 9. История буфера обмена с лимитом и очисткой
- Настройка лимита хранимых записей (от 5 до 100), кнопка быстрой очистки всей истории и 3-кратная экономия оперативной памяти за счёт оптимизированного декодирования превью картинок.

### 💻 10. Исправление прозрачности окон и поддержка встроенной графики (iGPU)
- Устранены артефакты заливки окон чёрным цветом и квадратные рамки на интегрированных видеокартах (Intel UHD/Iris, AMD Radeon):
  - Ликвидирован конфликт `DwmExtendFrameIntoClientArea` с композитором Windows 11 DirectComposition.
  - Сохранены нативные системные материалы Mica, Acrylic и Tabbed.
  - Принудительное обновление геометрии DWM через `SWP_FRAMECHANGED` с красивым закруглением `CornerRadius="8"`.

### 🎨 11. Сглаживание и High-DPI масштабирование визуализатора
- Высококачественная бикубическая интерполяция (`BitmapScalingMode="HighQuality"`) и зона антиалиасинга (2.5f) полностью устраняют пикселизацию спектрограммы на экранах ноутбуков с масштабированием 125–175%.

### 🏷️ 12. Визуальные бейджи форка (Fork Badges)
- Все эксклюзивные функции форка в настройках отмечены акцентными бейджами `FORK`.

### 🧹 13. Удаление телеметрии и Microsoft Store
- Полностью вырезаны фоновые сетевые запросы к серверам телеметрии и магазину Microsoft.

### 🔏 14. Цифровая подпись и компактный размер
- Все сборки подписаны цифровым сертификатом `CN=xXKoksMenXx` с меткой времени Sectigo RFC 3161 TSA.
- Исполняемый файл в сжатом архиве занимает всего **~84 МБ**.

---

## 📦 Скачивание и установка

Загрузите актуальную версию из раздела [**Releases**](https://github.com/KoksMen/FluentFlyout/releases/latest):

| Файл | Описание | Размер |
| :--- | :--- | :--- |
| **`FluentFlyout.exe`** | Автономный Single-File исполняемый файл (Self-Contained) | ~210 МБ |
| **`Portable_x64_AMD.zip`** | Портативный zip-архив (используется встроенным автообновлением) | ~84 МБ |
| **`FluentFlyout-v2.15.0-f2.3.0-Portable-x64.zip`** | Каталожная копия релиза | ~84 МБ |

> [!TIP]
> Установка .NET Runtime не требуется: среда выполнения уже встроена внутрь исполняемого файла!

---

## 🛠️ Сборка из исходного кода

Для самостоятельной сборки потребуется [.NET 10.0 SDK](https://dotnet.microsoft.com/download):

```powershell
# Клонирование репозитория
git clone https://github.com/KoksMen/FluentFlyout.git
cd FluentFlyout

# Сборка и публикация автономного Single-File релиза
dotnet publish FluentFlyoutWPF\FluentFlyout.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\win-x64
```

---

# 🇬🇧 FluentFlyout (In English)

## 💡 About This Project

**FluentFlyout (KoksMen Edition)** is an enhanced and performance-hardened fork of FluentFlyout for Windows 10/11.

It resolves gaming interruptions, taskbar latencies, integrated graphics rendering bugs, and external telemetry lock-in, providing a fast, lightweight (~84 MB compressed) and fully customizable flyout experience.

---

## 🌟 Key Features

- **🔄 Seamless In-App Auto-Updater:** Downloads GitHub Releases assets directly in-app, stages files, and updates automatically via batch script restart.
- **🔊 Reliable Volume Mixer & Native OSD Suppression:** Subscribes to endpoint volume events to reliably catch laptop Fn keys, DAC dials, mouse wheels, and software volume changes, while fully suppressing the Windows 10/11 native volume overlay.
- **🎮 Gaming Safeguards:** Separate toggles for exclusive and borderless fullscreen modes prevent games from minimizing.
- **⚡ Instant Taskbar Responsiveness:** Win32 fast path eliminates UI thread delays.
- **🔊 Mouse Wheel Volume Adjustment:** Scroll directly over taskbar elements to adjust system audio.
- **🎯 Browser Tab & Player Activation:** Click the flyout to focus the exact playing tab (Edge, Chrome, Firefox, Brave, Opera, Vivaldi) or desktop player (Spotify, Yandex Music, AIMP).
- **📐 Compact Taskbar Adaptation:** Supports small taskbars (StartAllBack / Windhawk).
- **🖱️ Taskbar Widget Context Menu:** Right-click controls for quick playback and player switching.
- **📋 Clipboard Limit & Cleanup:** Configurable history limit and RAM-efficient thumbnail caching.
- **💻 Window Backdrop & Translucency Fix:** Fixes black window backgrounds and supports all GPUs with clean Mica/Acrylic backdrops and rounded borders.
- **🎨 High-DPI Visualizer AA:** Bicubic scaling mode removes spectrogram pixelation.
- **🏷️ Fork Feature Badges:** Clear `FORK` markers in Settings for fork-exclusive features.
- **🧹 Zero Telemetry:** Removed all background telemetry and Microsoft Store dependencies.
- **🔏 Authenticode Code Signing:** Binaries signed with Sectigo RFC 3161 timestamp.

---

## 📜 Лицензия / License

Проект распространяется под лицензией **GNU General Public License v3.0 (GPL-3.0)**.  
Подробности доступны в файле [LICENSE](LICENSE).
