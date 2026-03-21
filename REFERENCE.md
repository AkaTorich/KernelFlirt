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

| Item / Пункт | EN | RU |
|---|---|---|
| Go Back | Return to previous location after navigation | Вернуться к предыдущей позиции после перехода |
| *Symbol right-click:* | | |
| Go to {symbol} | Navigate to symbol address | Перейти к адресу символа |
| Set breakpoint on {symbol} | Set BP at symbol entry | Поставить BP на символ |
| Copy symbol name | Copy symbol name to clipboard | Скопировать имя символа |
| Copy address | Copy symbol address to clipboard | Скопировать адрес символа |

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

*Generated for KernelFlirt v1.2.0 — 168+ menu items across 15 context menus, 6 main menus, and toolbar.*
