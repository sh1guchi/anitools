import os
import sys

# Поля в строке Dialogue:
# Layer(0), Start(1), End(2), Style(3), Name/актёр(4), ...
FIELD_STYLE = 3
FIELD_ACTOR = 4


def get_all_values(folder, field_index):
    value_example = {}
    ass_files = [f for f in os.listdir(folder) if f.lower().endswith(".ass")]

    for filename in ass_files:
        filepath = os.path.join(folder, filename)
        with open(filepath, "r", encoding="utf-8-sig") as f:
            for line in f:
                if line.startswith("Dialogue:"):
                    parts = line.split(",")
                    if len(parts) > field_index:
                        value = parts[field_index].strip()
                        if value not in value_example:
                            value_example[value] = filename

    return sorted(value_example), value_example, ass_files


def remove_lines(folder, ass_files, field_index, values_to_remove):
    total_removed = 0

    for filename in ass_files:
        filepath = os.path.join(folder, filename)
        with open(filepath, "r", encoding="utf-8-sig") as f:
            lines = f.readlines()

        new_lines = []
        removed = 0
        for line in lines:
            if line.startswith("Dialogue:"):
                parts = line.split(",")
                if len(parts) > field_index and parts[field_index].strip() in values_to_remove:
                    removed += 1
                    continue
            new_lines.append(line)

        with open(filepath, "w", encoding="utf-8-sig") as f:
            f.writelines(new_lines)

        total_removed += removed
        status = f"удалено строк: {removed}" if removed > 0 else "без изменений"
        print(f"  {filename} — {status}")

    return total_removed


def parse_input(raw, max_index):
    tokens = raw.replace(",", " ").split()
    indices = [int(t) for t in tokens]
    invalid = [i for i in indices if i < 1 or i > max_index]
    return indices, invalid


def main():
    folder = sys.argv[1] if len(sys.argv) > 1 else os.getcwd()

    if not os.path.isdir(folder):
        print(f"Ошибка: '{folder}' не является папкой.")
        sys.exit(1)

    print(f"Папка: {folder}\n")

    print("По какому полю фильтровать?")
    print("  [1] Стили")
    print("  [2] Актёры (Name)")
    print()

    while True:
        field_mode = input("Ваш выбор (1 или 2): ").strip()
        if field_mode in ("1", "2"):
            break
        print("Введите 1 или 2.")

    if field_mode == "1":
        field_index = FIELD_STYLE
        label = "стилей"
        label_one = "стили"
    else:
        field_index = FIELD_ACTOR
        label = "актёров"
        label_one = "актёров"

    print("\nСканирование файлов...\n")

    values, value_example, ass_files = get_all_values(folder, field_index)

    if not ass_files:
        print("Файлы .ass не найдены.")
        sys.exit(0)

    if not values:
        print(f"Значения ({label}) не найдены.")
        sys.exit(0)

    print(f"Найдено файлов: {len(ass_files)}")
    print(f"Найдено ({label}): {len(values)}\n")

    print(f"Доступные {label}:")
    for i, value in enumerate(values, 1):
        shown = value if value else "(пусто)"
        print(f"  [{i}] {shown}  (пример: {value_example[value]})")

    print()
    print("Что хотите сделать?")
    print(f"  [1] Выбрать {label_one} которые ОСТАВИТЬ (остальные удалятся)")
    print(f"  [2] Выбрать {label_one} которые УДАЛИТЬ (остальные останутся)")
    print()

    while True:
        mode = input("Ваш выбор (1 или 2): ").strip()
        if mode in ("1", "2"):
            break
        print("Введите 1 или 2.")

    print()
    if mode == "1":
        prompt = f"Номера ({label}) которые ОСТАВИТЬ (через пробел или запятую): "
    else:
        prompt = f"Номера ({label}) которые УДАЛИТЬ (через пробел или запятую): "

    while True:
        raw = input(prompt).strip()
        if not raw:
            print("Ничего не введено, попробуйте снова.")
            continue
        try:
            selected_indices, invalid = parse_input(raw, len(values))
        except ValueError:
            print("Ошибка: вводите только числа. Попробуйте снова.")
            continue
        if invalid:
            print(f"Ошибка: номера {invalid} вне диапазона. Попробуйте снова.")
            continue
        break

    selected = {values[i - 1] for i in selected_indices}

    if mode == "1":
        to_keep = selected
        to_remove = set(values) - to_keep
    else:
        to_remove = selected
        to_keep = set(values) - to_remove

    print()
    print("Оставляем:")
    for s in sorted(to_keep):
        print(f"  + {s if s else '(пусто)'}")
    print("Удаляем:")
    for s in sorted(to_remove):
        print(f"  - {s if s else '(пусто)'}")

    print()
    confirm = input("Продолжить? (д/н): ").strip().lower()
    if confirm not in ("д", "y", "да", "yes"):
        print("Отменено.")
        sys.exit(0)

    print()
    total = remove_lines(folder, ass_files, field_index, to_remove)
    print(f"\nГотово. Всего удалено строк: {total}")


if __name__ == "__main__":
    main()
