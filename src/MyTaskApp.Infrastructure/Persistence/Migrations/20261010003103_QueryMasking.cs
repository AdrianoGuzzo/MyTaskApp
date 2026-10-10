using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyTaskApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QueryMasking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Argument",
                table: "AnonymizationRules",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            // ADR-058: as regras deixam de ser expressões do PostgreSQL Anonymizer
            // e viram máscaras do catálogo. Kind: 1 = FUNCTION, 2 = VALUE. Method:
            // 1 Hash, 2 E-mail falso, 3 Parcial, 4 Nome falso, 5 Texto fixo,
            // 6 Número fixo, 7 Nulo, 8 Data deslocada, 9 Ruído. O que não tem
            // equivalente vira Hash; a validação da cópia aponta se não couber no tipo.
            migrationBuilder.Sql(
                """
                UPDATE "AnonymizationRules" SET
                    "Argument" = CASE
                        WHEN "Kind" = 2 AND upper(trim("Expression")) = 'NULL' THEN NULL
                        WHEN "Kind" = 2 AND "Expression" LIKE '''%' THEN replace(substr("Expression", 2, length("Expression") - 2), '''''', '''')
                        WHEN "Kind" = 2 THEN trim("Expression")
                        WHEN "Expression" LIKE 'anon.partial\_email%' ESCAPE '\' THEN NULL
                        WHEN "Expression" LIKE 'anon.partial(%' THEN
                            CAST(min(50, max(0, CAST(substr(substr("Expression", instr("Expression", ',') + 1), 1,
                                instr(substr("Expression", instr("Expression", ',') + 1), ',') - 1) AS INTEGER))) AS TEXT)
                            || ',' ||
                            CAST(min(50, max(0, CAST(replace(substr("Expression", instr("Expression", '$$,') + 3), ')', '') AS INTEGER))) AS TEXT)
                        WHEN "Expression" LIKE 'anon.random\_date%' ESCAPE '\' THEN '365'
                        WHEN "Expression" LIKE 'anon.noise(%' THEN
                            CAST(min(100, max(1, CAST(round(CAST(trim(replace(substr("Expression", instr("Expression", ',') + 1), ')', '')) AS REAL) * 100) AS INTEGER))) AS TEXT)
                        ELSE NULL
                    END,
                    "Kind" = CASE
                        WHEN "Kind" = 2 AND upper(trim("Expression")) = 'NULL' THEN 7
                        WHEN "Kind" = 2 AND "Expression" LIKE '''%' THEN 5
                        WHEN "Kind" = 2 THEN 6
                        WHEN "Expression" LIKE 'anon.partial\_email%' ESCAPE '\' THEN 2
                        WHEN "Expression" LIKE 'anon.partial(%' THEN 3
                        WHEN "Expression" LIKE 'anon.dummy\_name%' ESCAPE '\'
                            OR "Expression" LIKE 'anon.dummy\_first\_name%' ESCAPE '\'
                            OR "Expression" LIKE 'anon.dummy\_last\_name%' ESCAPE '\' THEN 4
                        WHEN "Expression" LIKE 'anon.random\_date%' ESCAPE '\' THEN 8
                        WHEN "Expression" LIKE 'anon.noise(%' THEN 9
                        ELSE 1
                    END;
                """);

            migrationBuilder.RenameColumn(
                name: "Kind",
                table: "AnonymizationRules",
                newName: "Method");

            migrationBuilder.DropColumn(
                name: "Expression",
                table: "AnonymizationRules");

            migrationBuilder.DropColumn(
                name: "PolicyName",
                table: "AnonymizationProfiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Argument",
                table: "AnonymizationRules");

            migrationBuilder.RenameColumn(
                name: "Method",
                table: "AnonymizationRules",
                newName: "Kind");

            migrationBuilder.AddColumn<string>(
                name: "Expression",
                table: "AnonymizationRules",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PolicyName",
                table: "AnonymizationProfiles",
                type: "TEXT",
                maxLength: 63,
                nullable: false,
                defaultValue: "");
        }
    }
}
