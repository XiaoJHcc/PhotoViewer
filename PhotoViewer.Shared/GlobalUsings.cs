// PhotoViewer.Shared 要被 Mac / iOS / Android 三个头以 Compile Include 链接编译。
// Apple 两个头开了 ImplicitUsings，Android 头没有；这里只补齐共享源实际用到的
// System 与 System.Collections.Generic（Math、List、IReadOnlyList 等），
// 不引入 Linq/Tasks 等更宽的隐式命名空间，避免改变 Android 头既有语义。
global using System;
global using System.Collections.Generic;
