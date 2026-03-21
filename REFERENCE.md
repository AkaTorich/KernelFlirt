# KernelFlirt v1.2.0 — Reference / Справочник

---

## Menu Bar / Главное меню

### File / Файл

| Item / Пункт | Shortcut | EN | RU |
|---|---|---|---|
| Open Process... | — | Select a running process to attach | Выбрать запущенный процесс для подключения |
| Open & Debug (.exe)... | — | Open remote executable and debug from start | Открыть удалённый .exe и начать отладку с точки входа |
| Connect Kernel... | — | Connect to kernel driver on remote machine | Подключиться к драйверу ядра на удалённой машине |
| Disconnect | — | Disconnect from kernel driver | Отключиться от драйвера ядра |
| Exit | — | Close KernelFlirt | Закрыть KernelFlirt |

### Debug / Отладка

| Item / Пункт | Shortcut | EN | RU |
|---|---|---|---|
| Attach (by PID) | — | Attach to process by PID entered in toolbar | Подключиться к процессу по PID из панели инструментов |
| Detach | — | Detach from current process | Отключиться от текущего процесса |
| Run | F9 | Start/resume process execution | Запустить/возобновить выполнение |
| Pause | F12 | Pause execution (break into debugger) | Приостановить выполнение (вход в отладчик) |
| Continue | F5 | Continue execution after break | Продолжить выполнение после остановки |
| Step Into | F7 | Execute one instruction, following into calls | Выполнить одну инструкцию, входя в вызовы |
| Step Over | F8 | Execute one instruction, stepping over calls | Выполнить одну инструкцию, перешагивая вызовы |
| Step Out | Ctrl+F9 | Execute until current function returns | Выполнить до возврата из текущей функции |
| Skip Instruction | Ctrl+F8 | Skip current instruction (advance RIP without executing) | Пропустить инструкцию (сдвинуть RIP без выполнения) |
| Run to Cursor | F4 | Execute until reaching selected address | Выполнить до выбранного адреса |
| Toggle Breakpoint | F2 | Set/remove software breakpoint at selected address | Установить/убрать программный брейкпоинт |
| Toggle HW Breakpoint | — | Set/remove hardware execution breakpoint (DR0-DR3) | Установить/убрать аппаратный брейкпоинт на выполнение |
| Toggle HW Write Watch | — | Set/remove hardware write watchpoint | Установить/убрать аппаратный watchpoint на запись |
| Toggle HW R/W Watch | — | Set/remove hardware read/write watchpoint | Установить/убрать аппаратный watchpoint на чтение/запись |
| Toggle Memory Breakpoint | — | Set PAGE_GUARD breakpoint on memory page | Установить брейкпоинт PAGE_GUARD на страницу памяти |
| Set Conditional Breakpoint... | — | Set breakpoint with condition expression | Установить условный брейкпоинт |
| Set Log Breakpoint... | — | Set breakpoint that logs instead of breaking | Установить логирующий брейкпоинт (без остановки) |
| Remove All Breakpoints | — | Remove all breakpoints | Удалить все брейкпоинты |

### Search / Поиск

| Item / Пункт | Shortcut | EN | RU |
|---|---|---|---|
| Binary Pattern... | Ctrl+F | Search for hex byte pattern in memory | Поиск шестнадцатеричного паттерна в памяти |
| String References... | — | Search for string references in modules | Поиск строковых ссылок в модулях |
| Intermodular Calls | — | Find calls between modules (IAT calls) | Найти вызовы между модулями (вызовы через IAT) |

### View / Вид

| Item / Пункт | Shortcut | EN | RU |
|---|---|---|---|
| Refresh All | — | Refresh all views (registers, stack, modules, etc.) | Обновить все представления (регистры, стек, модули и т.д.) |
| Go to RIP | Ctrl+G | Navigate disassembler to current RIP | Перейти в дизассемблере к текущему RIP |
| Add Bookmark... | — | Add bookmark at address with comment | Добавить закладку с комментарием на адрес |
| Refresh SEH Chain | — | Refresh Structured Exception Handler chain | Обновить цепочку обработчиков исключений (SEH) |
| Restore All Patches | — | Undo all memory patches (restore original bytes) | Отменить все патчи памяти (вернуть оригинальные байты) |
| Fullscreen | F11 | Toggle fullscreen mode | Переключить полноэкранный режим |
| Settings... | — | Open settings window (themes, colors, paths) | Открыть окно настроек (темы, цвета, пути) |

### Symbols / Символы

| Item / Пункт | Shortcut | EN | RU |
|---|---|---|---|
| Load All Symbols | — | Download and load PDB symbols for all modules | Загрузить PDB-символы для всех модулей |
| Set Symbol Path... | — | Configure symbol server and local paths | Настроить сервер символов и локальные пути |
| Clear Symbol Cache | — | Delete cached symbol files | Очистить кэш символов |

### Plugins / Плагины

| Item / Пункт | Shortcut | EN | RU |
|---|---|---|---|
| Settings... | — | Open plugin settings (colors, configurations) | Открыть настройки плагинов (цвета, конфигурации) |
| *[Plugin items]* | — | Dynamic items added by loaded plugins | Динамические пункты от загруженных плагинов |

### Help / Помощь

| Item / Пункт | Shortcut | EN | RU |
|---|---|---|---|
| About | — | Show version and build info | Показать версию и информацию о сборке |

---

## Toolbar / Панель инструментов

| Button / Кнопка | Shortcut | EN | RU |
|---|---|---|---|
| Open | — | Open process picker | Открыть выбор процесса |
| Debug | — | Open & Debug remote executable | Открыть и отладить удалённый .exe |
| Connect | — | Connect to kernel driver | Подключиться к драйверу ядра |
| Disconnect | — | Disconnect from kernel | Отключиться от ядра |
| PID: [text field] | — | Enter PID for attach | Ввести PID для подключения |
| Attach | — | Attach to process by PID | Подключиться по PID |
| Detach | — | Detach from process | Отключиться от процесса |
| Run | F9 | Run | Запуск |
| Pause | F12 | Pause | Пауза |
| F5 | F5 | Continue | Продолжить |
| F7 | F7 | Step Into | Шаг внутрь |
| F8 | F8 | Step Over | Шаг через |
| Out | Ctrl+F9 | Step Out | Шаг наружу |
| F4 | F4 | Run to Cursor | Выполнить до курсора |
| BP | F2 | Toggle breakpoint | Брейкпоинт |
| HW | — | Hardware execution breakpoint | Аппаратный BP (выполнение) |
| WW | — | Hardware write watchpoint | Аппаратный BP (запись) |
| RW | — | Hardware R/W watchpoint | Аппаратный BP (чтение/запись) |
| Mem | — | Memory breakpoint (PAGE_GUARD) | Брейкпоинт памяти (PAGE_GUARD) |
| Addr: [text field] | — | Enter hex address | Ввести адрес (hex) |
| Go | — | Navigate disassembler to address | Перейти по адресу в дизассемблере |

---

## Context Menus / Контекстные меню

### Disassembler / Дизассемблер

| Item / Пункт | Shortcut | EN | RU |
|---|---|---|---|
| Toggle Breakpoint | F2 | Toggle software breakpoint at address | Переключить программный BP на адресе |
| Toggle HW Breakpoint | — | Toggle hardware execution breakpoint | Переключить аппаратный BP |
| Set Conditional Breakpoint... | — | Set breakpoint with condition expression | Установить условный BP |
| Set Log Breakpoint... | — | Set logging breakpoint (no break) | Установить логирующий BP |
| Run to Cursor | F4 | Execute until this address | Выполнить до этого адреса |
| Skip Instruction | Ctrl+F8 | Skip instruction (advance RIP) | Пропустить инструкцию (сдвинуть RIP) |
| Set RIP Here | — | Move RIP to selected address | Переместить RIP на выбранный адрес |
| Add Bookmark... | — | Bookmark this address | Добавить закладку на адрес |
| Follow in Dump | — | Show address in hex dump | Показать адрес в hex-дампе |
| Follow in Disassembler | — | Navigate to operand address | Перейти по адресу операнда |
| Go Back | — | Return to previous location | Вернуться к предыдущей позиции |
| Copy Address | — | Copy instruction address | Скопировать адрес инструкции |
| Copy Line | — | Copy disassembly line | Скопировать строку дизассемблера |
| Copy All | — | Copy all visible disassembly | Скопировать весь видимый дизасм |
| Search Binary... | — | Search hex pattern from here | Поиск hex-паттерна |
| Search Strings... | — | Search text strings from here | Поиск текстовых строк |
| Decompile Function | — | Decompile function at this address | Декомпилировать функцию |
| | | | |
| *Symbol right-click:* | | | |
| Go to {symbol} | — | Navigate to symbol address | Перейти к адресу символа |
| Set breakpoint on {symbol} | — | Set BP at symbol entry | Поставить BP на символ |
| Copy symbol name | — | Copy symbol name to clipboard | Скопировать имя символа |
| Copy address | — | Copy symbol address to clipboard | Скопировать адрес символа |

### Decompiler Output / Вывод декомпилятора

| Item / Пункт | Shortcut | EN | RU |
|---|---|---|---|
| Copy | Ctrl+C | Copy selected text | Скопировать выделенный текст |
| Select All | Ctrl+A | Select all text | Выделить всё |
| Copy All | — | Copy entire decompiled output | Скопировать весь псевдокод |

### Registers / Регистры

| Item / Пункт | EN | RU |
|---|---|---|
| Copy All Registers | Copy all register values to clipboard | Скопировать все регистры в буфер |
| Follow in Dump | Show register value address in hex dump | Показать адрес значения регистра в hex-дампе |
| Follow in Disassembler | Navigate disasm to register value | Перейти в дизассемблере к значению регистра |

### Breakpoints / Брейкпоинты

| Item / Пункт | EN | RU |
|---|---|---|
| Go to Address | Navigate disasm to breakpoint address | Перейти к адресу брейкпоинта |
| Follow in Dump | Show breakpoint address in hex dump | Показать адрес BP в hex-дампе |
| Decompile | Decompile function at breakpoint | Декомпилировать функцию на BP |
| Remove | Remove this breakpoint | Удалить брейкпоинт |
| Remove All | Remove all breakpoints | Удалить все брейкпоинты |

### Modules / Модули

| Item / Пункт | EN | RU |
|---|---|---|
| Follow in Disassembler | Navigate to module base in disasm | Перейти к базе модуля в дизассемблере |
| Follow in Dump | Show module in hex dump | Показать модуль в hex-дампе |
| Copy Base Address | Copy module base address | Скопировать базовый адрес модуля |
| Show Imports | List all imports of this module | Показать импорты модуля |
| Show Functions | List all exported/known functions | Показать функции модуля |

### Kernel Modules / Модули ядра

| Item / Пункт | EN | RU |
|---|---|---|
| Go to Entry Point | Navigate to driver entry point | Перейти к точке входа драйвера |
| Go to Base | Navigate to module base | Перейти к базе модуля |
| Follow in Dump | Show in hex dump | Показать в hex-дампе |
| Copy Base Address | Copy base address | Скопировать базовый адрес |
| Copy Name | Copy module name | Скопировать имя модуля |
| Show Imports | List module imports | Показать импорты |
| Show Functions | List module functions | Показать функции |

### Threads / Потоки

| Item / Пункт | EN | RU |
|---|---|---|
| Switch to Thread | Switch debugger context to this thread | Переключиться на поток |
| Suspend | Suspend thread execution | Приостановить поток |
| Resume | Resume suspended thread | Возобновить поток |
| Follow Start Address | Navigate to thread start function | Перейти к стартовой функции потока |

### Call Stack / Стек вызовов

| Item / Пункт | EN | RU |
|---|---|---|
| Follow in Disassembler | Navigate to return address | Перейти к адресу возврата |
| Follow in Dump | Show stack frame in dump | Показать фрейм стека в дампе |
| Decompile | Decompile function at return address | Декомпилировать функцию |
| Copy Address | Copy return address | Скопировать адрес возврата |

### Bookmarks / Закладки

| Item / Пункт | EN | RU |
|---|---|---|
| Go to Bookmark | Navigate to bookmarked address | Перейти к адресу закладки |
| Follow in Dump | Show in hex dump | Показать в hex-дампе |
| Remove | Delete bookmark | Удалить закладку |

### Patches / Патчи

| Item / Пункт | EN | RU |
|---|---|---|
| Restore Original | Restore original bytes for this patch | Восстановить оригинальные байты |
| Restore All | Restore all patches | Восстановить все патчи |
| Go to Address | Navigate to patched address | Перейти к адресу патча |

### Exceptions (SEH) / Исключения (SEH)

| Item / Пункт | EN | RU |
|---|---|---|
| Follow Start in Disassembler | Navigate to handler start | Перейти к началу обработчика |
| Follow End in Disassembler | Navigate to handler end | Перейти к концу обработчика |
| Follow in Dump | Show handler in hex dump | Показать обработчик в hex-дампе |
| Decompile | Decompile exception handler | Декомпилировать обработчик |
| Set Breakpoint at Start | Set BP at handler entry | Поставить BP на начало обработчика |
| Set Breakpoint at End | Set BP at handler end | Поставить BP на конец обработчика |
| Copy Address | Copy handler address | Скопировать адрес обработчика |
| Copy Function Name | Copy handler function name | Скопировать имя функции |
| Copy Line | Copy full line info | Скопировать строку целиком |
| Show Unwind Info | Display exception unwind metadata | Показать информацию раскрутки стека |

### Sections / Секции

| Item / Пункт | EN | RU |
|---|---|---|
| Follow in Disassembler | Navigate to section start | Перейти к началу секции |
| Follow in Dump | Show section in hex dump | Показать секцию в hex-дампе |
| Memory BP on Section (PAGE_GUARD, all pages) | Set PAGE_GUARD on all section pages | PAGE_GUARD на все страницы секции |
| Dump Section to File... | Export section bytes to disk | Сохранить секцию в файл |
| Fill Section with NOPs (0x90) | Overwrite section with NOP instructions | Заполнить секцию NOP-ами (0x90) |
| Fill Section with Zeros (0x00) | Overwrite section with zero bytes | Заполнить секцию нулями (0x00) |
| Search Binary in Section... | Search hex pattern within section | Поиск hex-паттерна в секции |
| Search String in Section... | Search text within section | Поиск текста в секции |
| Copy Address | Copy section virtual address | Скопировать виртуальный адрес секции |
| Copy Section Name | Copy section name (e.g. .text) | Скопировать имя секции |
| Copy Line | Copy full section info | Скопировать строку целиком |

### Strings / Строки

| Item / Пункт | EN | RU |
|---|---|---|
| Follow in Disassembler | Navigate to string address in disasm | Перейти к адресу строки в дизассемблере |
| Follow in Dump | Show string in hex dump | Показать строку в hex-дампе |
| Set Breakpoint | Set BP at string address | Поставить BP на адрес строки |
| Copy Address | Copy string address | Скопировать адрес строки |
| Copy String | Copy string content | Скопировать содержимое строки |
| Copy Line | Copy full line with metadata | Скопировать строку с метаданными |

### Imports / Импорты

| Item / Пункт | EN | RU |
|---|---|---|
| Follow in Disassembler | Navigate to import in disasm | Перейти к импорту в дизассемблере |
| Follow in Dump | Show IAT entry in hex dump | Показать запись IAT в hex-дампе |
| Decompile | Decompile imported function | Декомпилировать импортированную функцию |
| Set Breakpoint on Function | Set BP at import function entry | Поставить BP на функцию импорта |
| Copy Address | Copy import info | Скопировать информацию об импорте |

### Functions / Функции

| Item / Пункт | EN | RU |
|---|---|---|
| Follow in Disassembler | Navigate to function | Перейти к функции |
| Decompile | Decompile function | Декомпилировать функцию |
| Set Breakpoint | Set BP at function entry | Поставить BP на вход в функцию |
| Copy Address | Copy function name and address | Скопировать имя и адрес функции |

### Search Results / Результаты поиска

| Item / Пункт | EN | RU |
|---|---|---|
| Follow in Disassembler | Navigate to result address | Перейти к адресу результата |
| Follow in Dump | Show result in hex dump | Показать результат в hex-дампе |
| Decompile | Decompile function at result | Декомпилировать функцию |
| Set Breakpoint | Set BP at result address | Поставить BP на адрес результата |

### Stack / Стек

| Item / Пункт | EN | RU |
|---|---|---|
| Follow in Dump | Show stack value in hex dump | Показать значение стека в hex-дампе |
| Follow in Disassembler | Navigate to stack value address | Перейти по адресу значения стека |
| Copy | Copy stack entry | Скопировать запись стека |

### Hex Dump / Hex-дамп

| Item / Пункт | EN | RU |
|---|---|---|
| Copy Address | Copy address of selected line | Скопировать адрес выбранной строки |
| Copy Hex (Line) | Copy hex bytes of current line | Скопировать hex-байты текущей строки |
| Copy Hex (All) | Copy all hex bytes | Скопировать все hex-байты |
| Copy ASCII (Line) | Copy ASCII of current line | Скопировать ASCII текущей строки |
| Copy ASCII (All) | Copy all ASCII data | Скопировать все ASCII-данные |
| Copy Line | Copy full formatted line | Скопировать форматированную строку |
| Copy All | Copy entire hex dump | Скопировать весь hex-дамп |
| Follow in Disassembler | Navigate to address in disasm | Перейти по адресу в дизассемблере |
| Set Memory Breakpoint (PAGE_GUARD) | Set PAGE_GUARD on memory page | Установить PAGE_GUARD на страницу памяти |
| Set HW Write Watchpoint | Set hardware write watchpoint | Установить аппаратный watchpoint на запись |
| Set HW Read/Write Watchpoint | Set hardware R/W watchpoint | Установить аппаратный watchpoint на чтение/запись |
| Search Binary... | Search for hex byte pattern | Поиск hex-паттерна |
| Search String... | Search for text string | Поиск текстовой строки |

### Log / Лог

| Item / Пункт | EN | RU |
|---|---|---|
| Copy All | Copy all log messages | Скопировать все сообщения лога |
| Clear | Clear log | Очистить лог |

---

## Keyboard Shortcuts / Горячие клавиши

| Key / Клавиша | EN | RU |
|---|---|---|
| F2 | Toggle breakpoint | Переключить брейкпоинт |
| F4 | Run to cursor | Выполнить до курсора |
| F5 | Continue execution | Продолжить выполнение |
| F7 | Step Into | Шаг внутрь |
| F8 | Step Over | Шаг через |
| F9 | Run | Запуск |
| F11 | Toggle fullscreen | Полноэкранный режим |
| F12 | Pause | Пауза |
| Ctrl+F | Search binary pattern | Поиск бинарного паттерна |
| Ctrl+F8 | Skip instruction | Пропустить инструкцию |
| Ctrl+F9 | Step Out | Шаг наружу |
| Ctrl+G | Go to RIP | Перейти к RIP |
| Ctrl+C | Copy (in decompiler/hex dump) | Копировать |
| Ctrl+A | Select All (in decompiler) | Выделить всё |

---

## Remote File Browser / Удалённый файловый браузер

*Opens via File → Open & Debug / Открывается через Файл → Открыть и отладить*

| Action / Действие | Shortcut | EN | RU |
|---|---|---|---|
| Open & Debug | Double-click .exe/.sys | Open file in debugger | Открыть файл в отладчике |
| Download | Double-click other files | Download file to host | Скачать файл на хост |
| Upload | Drag & Drop | Upload file from host to VM | Загрузить файл с хоста на VM |
| New Folder | Context menu | Create new directory | Создать новую папку |
| Rename | F2 | Rename file or folder | Переименовать файл или папку |
| Delete | Del | Delete file or folder | Удалить файл или папку |
| Copy Path | Context menu | Copy full remote path | Скопировать полный путь |
| Back | Alt+← / Backspace | Go back in navigation history | Назад по истории навигации |
| Forward | Alt+→ | Go forward in navigation history | Вперёд по истории навигации |
| Up | Toolbar | Go to parent directory | Перейти в родительскую папку |
| Refresh | F5 | Refresh directory listing | Обновить список файлов |

---

## Dialogs / Диалоговые окна

### Process Picker / Выбор процесса

| Button / Кнопка | EN | RU |
|---|---|---|
| Refresh | Refresh process list | Обновить список процессов |
| Attach | Attach to selected process | Подключиться к выбранному процессу |
| Cancel | Close dialog | Закрыть диалог |

### Settings / Настройки

| Control / Элемент | EN | RU |
|---|---|---|
| Theme selector (ComboBox) | Select theme preset | Выбрать тему оформления |
| Load All | Load all colors from selected theme | Загрузить все цвета из выбранной темы |
| Save As... | Save current colors as new theme | Сохранить текущие цвета как новую тему |
| Color pickers (per key) | Click to pick color for each UI element | Выбрать цвет для каждого элемента UI |
| Plugin tab Fg/Bg pickers | Per-plugin tab header color overrides | Цвета заголовков вкладок для каждого плагина |
| Reset Defaults | Reset all settings to defaults | Сбросить все настройки по умолчанию |
| OK | Apply and close | Применить и закрыть |
| Cancel | Discard and close | Отменить и закрыть |

### Plugin Settings / Настройки плагинов

| Control / Элемент | EN | RU |
|---|---|---|
| Enabled (CheckBox per plugin) | Enable/disable plugin | Включить/выключить плагин |
| Close | Close plugin settings | Закрыть настройки плагинов |

### Color Picker / Выбор цвета

| Control / Элемент | EN | RU |
|---|---|---|
| 42 color presets | Quick-select common colors | Быстрый выбор типовых цветов |
| Hex input field | Enter color as #RRGGBB | Ввести цвет в формате #RRGGBB |
| OK | Accept selected color | Принять выбранный цвет |
| Cancel | Discard selection | Отменить выбор |

### Input Dialog / Диалог ввода

| Control / Элемент | EN | RU |
|---|---|---|
| Text field | Enter value (address, name, etc.) | Ввести значение (адрес, имя и т.д.) |
| OK | Accept input | Принять ввод |
| Cancel | Cancel input | Отменить ввод |

---

## Plugins / Плагины

### AI Assistant / ИИ-ассистент

**Chat Panel / Панель чата:**

| Control / Элемент | Shortcut | EN | RU |
|---|---|---|---|
| Message input (TextBox) | Enter | Type message to AI | Ввести сообщение для ИИ |
| Send (Button) | Enter | Send message | Отправить сообщение |
| Settings (⚙ Button) | — | Open AI provider settings | Открыть настройки провайдера ИИ |
| Clear chat | — | Clear conversation history | Очистить историю чата |

**AI Settings Dialog / Настройки ИИ:**

| Control / Элемент | EN | RU |
|---|---|---|
| Provider (ComboBox) | Select AI provider (DeepSeek, OpenAI, Anthropic, Ollama, LM Studio, Qwen, Custom) | Выбрать провайдера ИИ |
| API Endpoint (TextBox) | API URL (auto-filled per provider) | URL API (заполняется автоматически) |
| API Key (TextBox) | API key (leave empty for local providers) | API ключ (пусто для локальных провайдеров) |
| Model (TextBox) | Model name (auto-filled per provider) | Название модели |
| Max Tokens (Slider) | Maximum response tokens (1–8192) | Максимум токенов ответа |
| Temperature (Slider) | Response randomness (0.0–1.0) | Случайность ответа |
| System Prompt (TextBox) | System prompt for AI behavior | Системный промпт для поведения ИИ |
| Reset to Default | Reset all settings to defaults | Сбросить настройки по умолчанию |
| OK | Save settings | Сохранить настройки |
| Cancel | Discard changes | Отменить изменения |

**Context Toggles / Контекстные переключатели:**

| CheckBox | EN | RU |
|---|---|---|
| Registers | Include CPU registers in AI context | Включить регистры CPU в контекст ИИ |
| Disasm | Include disassembly at RIP | Включить дизасм на RIP |
| Stack | Include stack dump | Включить дамп стека |
| Modules | Include loaded modules | Включить загруженные модули |
| Threads | Include thread list | Включить список потоков |
| Breakpoints | Include breakpoint list | Включить список BP |

**AI Tools (used by AI automatically) / Инструменты ИИ (используются автоматически):**

| Tool / Инструмент | EN | RU |
|---|---|---|
| decompile | Decompile function to C pseudocode | Декомпилировать функцию в псевдокод C |
| disassemble | Disassemble instructions at address | Дизассемблировать инструкции по адресу |
| read_memory | Read memory bytes at address | Прочитать байты памяти по адресу |
| write_memory | Write bytes to memory | Записать байты в память |
| read_registers | Read all CPU registers | Прочитать все регистры CPU |
| resolve_symbol | Resolve symbol name ↔ address | Разрешить имя символа ↔ адрес |
| list_modules | List loaded modules | Показать загруженные модули |
| list_threads | List process threads | Показать потоки процесса |
| set_breakpoint | Set software breakpoint | Установить программный BP |
| remove_breakpoint | Remove breakpoint by handle | Удалить BP по handle |
| list_breakpoints | List all breakpoints | Показать все BP |
| continue_execution | Continue (F5) | Продолжить выполнение |
| single_step | Step Into (F7) | Шаг внутрь |
| step_over | Step Over (F8) | Шаг через |
| step_out | Step Out (Ctrl+F9) | Шаг наружу |
| run_to_address | Run to specific address | Выполнить до адреса |
| skip_instruction | Skip instruction (Ctrl+F8) | Пропустить инструкцию |
| pause_execution | Pause (F12) | Приостановить |
| wait_for_break | Wait for process to stop | Ожидать остановки процесса |
| navigate_disasm | Navigate disassembler view | Перейти в дизассемблере |
| disasm_go_back | Go back in disassembler history | Вернуться назад в дизассемблере |

### Anti-Debug / Антиотладка

**Buttons / Кнопки:**

| Button / Кнопка | EN | RU |
|---|---|---|
| Apply Now | Apply all checked anti-debug patches | Применить все отмеченные патчи антиотладки |
| Check Status | Check current anti-debug status | Проверить текущий статус антиотладки |
| Select All | Enable all checkboxes | Включить все чекбоксы |
| Deselect All | Disable all checkboxes | Выключить все чекбоксы |
| Analyze Protector | Scan process for packer/protector patterns | Сканировать процесс на паттерны протектора |
| Jump to OEP | Set RIP to discovered OEP (after unpacking) | Установить RIP на найденный OEP |
| Dump PE | Dump all sections of unpacked PE to file | Дамп секций распакованного PE в файл |

**PEB Group / Группа PEB:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| PEB.BeingDebugged = 0 | ON | IsDebuggerPresent — zero the flag | Обнуление флага IsDebuggerPresent |
| PEB.NtGlobalFlag = 0 | ON | Clear FLG_HEAP_* debug flags | Очистить отладочные флаги кучи |
| ProcessHeap.Flags | ON | Set Flags=HEAP_GROWABLE, ForceFlags=0 | Flags=HEAP_GROWABLE, ForceFlags=0 |
| Zero STARTUPINFO fields | OFF | Zero dwFlags, wShowWindow in PEB | Обнуление dwFlags, wShowWindow в PEB |
| Patch PEB.OSBuildNumber | OFF | VMProtect Win10 2019+ check bypass | Обход проверки VMProtect Win10 2019+ |

**Kernel Debugger / Отладчик ядра:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| KdDebuggerEnabled = FALSE | OFF | Patch kernel debugger enabled flag | Патч флага отладчика ядра |
| KdDebuggerNotPresent = TRUE | OFF | Patch kernel debugger not present flag | Патч флага отсутствия отладчика ядра |

**NtQueryInformationProcess:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| Clear DebugPort | ON | Clear EPROCESS.DebugPort (defeats DebugPort/DebugObjectHandle/DebugFlags) | Очистить EPROCESS.DebugPort |
| DebugObjectHandle | ON | Cleared by DebugPort zeroing | Очищается через обнуление DebugPort |
| DebugFlags | ON | Cleared by DebugPort zeroing | Очищается через обнуление DebugPort |

**NtQuerySystemInformation:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| SystemKernelDebugger (class 0x23) | OFF | Hook to spoof. WARNING: PatchGuard BSOD! | Хук для спуфинга. ВНИМАНИЕ: BSOD от PatchGuard! |

**NtSetInformationThread:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| ThreadHideFromDebugger | ON | Clear HideFromDebugger bit in all threads | Очистить бит HideFromDebugger во всех потоках |

**NtClose:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| NtClose | ON | Cleared by DebugPort zeroing (no debug object) | Очищается обнулением DebugPort |

**NtQueryObject:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| NtQueryObject | OFF | Hook to hide DebugObject type from enumeration | Хук для скрытия типа DebugObject |

**NtCreateThreadEx:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| NtCreateThreadEx | OFF | Strip THREAD_CREATE_FLAGS_HIDE_FROM_DEBUGGER | Убрать флаг HIDE_FROM_DEBUGGER |

**Window Detection / Обнаружение окон:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| FindWindow | OFF | Hook NtUserFindWindowEx to hide debugger windows | Хук для скрытия окон отладчика |

**Hardware Breakpoints / DRx Protection:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| Hide DR0-DR3 | OFF | Zero DR0-DR3 in target thread context | Обнулить DR0-DR3 в контексте потока |
| NtGetContextThread | OFF | Hook to zero DR0-DR7 in returned CONTEXT | Хук для обнуления DR в возвращаемом CONTEXT |
| NtSetContextThread | OFF | Hook to prevent clearing HW breakpoints | Хук для защиты аппаратных BP |

**Timing Checks / Проверки времени:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| Patch RDTSC/CPUID | OFF | NOP out timing instructions. WARNING: breaks CRC checks! | NOP тайминг-инструкций. ВНИМАНИЕ: ломает CRC! |
| GetTickCount | OFF | Hook to return consistent incremental values | Хук для стабильных значений |
| QueryPerformanceCounter | OFF | Hook to normalize timing | Хук для нормализации тайминга |

**Miscellaneous / Разное:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| OutputDebugString | OFF | Hook to set LastError correctly | Хук для корректного LastError |
| BlockInput | OFF | Hook to prevent locking user input | Хук для предотвращения блокировки ввода |
| NtYieldExecution | OFF | Hook to return STATUS_NO_YIELD_PERFORMED | Хук для возврата NO_YIELD |
| Remove SeDebugPrivilege | OFF | Remove debug privilege from token | Убрать SeDebugPrivilege из токена |

**Automation / Автоматизация:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| Auto-apply on break | ON | Automatically apply patches when debugger breaks | Автоматически применять патчи при остановке |
| Auto-detect OEP | OFF | Detect unpacked PE and break at entry. WARNING: slows Themida! | Обнаружить OEP распакованного PE. ВНИМАНИЕ: замедляет Themida! |

### API Monitor / Мониторинг API

**Buttons / Кнопки:**

| Button / Кнопка | EN | RU |
|---|---|---|
| Start | Start API monitoring (set breakpoints on selected APIs) | Начать мониторинг API (BP на выбранные API) |
| Stop | Stop monitoring and remove breakpoints | Остановить мониторинг и убрать BP |
| Clear | Clear captured call log | Очистить лог вызовов |
| Export CSV | Export captured calls to CSV file | Экспорт вызовов в CSV файл |

**Filters / Фильтры:**

| Control / Элемент | EN | RU |
|---|---|---|
| Filter (TextBox) | Filter captured calls by text | Фильтр вызовов по тексту |
| Category (ComboBox) | Filter by category: All, File, Registry, Process, Memory, Library, Network, Misc | Фильтр по категории |

**API Categories / Категории API (CheckBoxes):**

| Category | EN | RU |
|---|---|---|
| File | File system APIs (CreateFile, ReadFile, WriteFile, etc.) | API файловой системы |
| Registry | Registry APIs (RegOpenKey, RegQueryValue, etc.) | API реестра |
| Process | Process APIs (CreateProcess, OpenProcess, etc.) | API процессов |
| Memory | Memory APIs (VirtualAlloc, VirtualProtect, etc.) | API памяти |
| Library | Library APIs (LoadLibrary, GetProcAddress, etc.) | API библиотек |
| Network | Network APIs (connect, send, recv, etc.) | API сети |
| Misc | Miscellaneous APIs | Разные API |

**Results Grid / Таблица результатов:**

| Column / Колонка | EN | RU |
|---|---|---|
| # | Call index | Номер вызова |
| Time | Timestamp of call | Время вызова |
| TID | Thread ID (hex) | ID потока (hex) |
| Module | Calling module name | Имя вызывающего модуля |
| Function | API function name | Имя API функции |
| Arguments | Function arguments | Аргументы функции |
| Return | Return value | Возвращаемое значение |

### Themida / Themida

**Buttons / Кнопки:**

| Button / Кнопка | EN | RU |
|---|---|---|
| Detect | Detect Themida/WinLicense protection | Обнаружить защиту Themida/WinLicense |
| Unpack | Start automated unpacking | Запустить автоматическую распаковку |
| Fix IAT | Manually fix Import Address Table | Вручную восстановить таблицу импортов |
| Dump PE | Dump unpacked PE to file | Дамп распакованного PE в файл |
| Stop | Stop unpacking process | Остановить распаковку |

**Settings / Настройки (CheckBoxes):**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| Auto-fix IAT after OEP | ON | Automatically reconstruct IAT when OEP is reached | Автоматически восстановить IAT при достижении OEP |
| Auto-dump PE after IAT fix | ON | Automatically dump PE after IAT reconstruction | Автоматически дамп PE после восстановления IAT |

### String Decryptor / Расшифровщик строк

**Configuration / Конфигурация:**

| Control / Элемент | EN | RU |
|---|---|---|
| Function address (TextBox) | Decrypt function address: `0x140001000`, `module.exe+0x1234`, `mod!FuncName` | Адрес функции дешифровки |
| Result location (ComboBox) | Where decrypted string is returned: RAX, RCX, RDX, R8, [RSP+offset], Fixed address | Где находится расшифрованная строка |
| Extra param (TextBox) | Offset or address for [RSP+offset] / Fixed address modes | Смещение или адрес |

**CheckBoxes:**

| CheckBox | Default | EN | RU |
|---|---|---|---|
| Unicode (UTF-16) | OFF | Treat decrypted strings as UTF-16 | Расшифрованные строки как UTF-16 |
| Auto-continue after capture | ON | Automatically continue execution after capturing string | Автоматически продолжить после захвата строки |

**Buttons / Кнопки:**

| Button / Кнопка | EN | RU |
|---|---|---|
| Start | Start string tracing (set BP on decrypt function) | Начать трассировку строк (BP на функцию дешифровки) |
| Stop | Stop tracing and remove breakpoints | Остановить трассировку и убрать BP |
| Clear | Clear captured strings | Очистить захваченные строки |
| Copy All | Copy all decrypted strings to clipboard | Скопировать все строки в буфер |

**Results Grid / Таблица результатов:**

| Column / Колонка | EN | RU |
|---|---|---|
| # | Capture index | Номер захвата |
| Caller | Return address of caller (hex) | Адрес возврата вызывающего (hex) |
| Symbol | Caller symbol name (if resolved) | Имя символа вызывающего |
| Ptr | Pointer to decrypted string (hex) | Указатель на расшифрованную строку (hex) |
| Enc | Encoding (A=ASCII, U=UTF-16) | Кодировка (A=ASCII, U=UTF-16) |
| Decrypted String | Captured decrypted string value | Захваченная расшифрованная строка |

---

