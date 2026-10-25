-- Esquema legado do schema settings (migrations InitialCreate, AddLockoutParameters, AddRegistrationParameters e
-- AddPasswordResetParameters do antigo projeto Settings), gerado com `dotnet ef migrations script --idempotent` antes
-- da retirada do projeto. Os testes o aplicam para reproduzir um banco que ainda tem parâmetros em settings e provar
-- a migração OwnIdentityParameters (que copia e valida parâmetros e histórico para o schema identity).

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'settings') THEN
        CREATE SCHEMA settings;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS settings."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023004421_InitialCreate') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'settings') THEN
            CREATE SCHEMA settings;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023004421_InitialCreate') THEN
    CREATE TABLE settings.parameter_history (
        id bigint GENERATED ALWAYS AS IDENTITY,
        parameter_key character varying(200) NOT NULL,
        revision integer NOT NULL,
        previous_value text,
        new_value text NOT NULL,
        previous_description text,
        new_description text NOT NULL,
        data_type character varying(20) NOT NULL,
        validation_pattern text,
        is_sensitive boolean NOT NULL,
        changed_by uuid,
        changed_at timestamptz NOT NULL,
        external_id uuid NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        CONSTRAINT pk_parameter_history PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023004421_InitialCreate') THEN
    CREATE TABLE settings.parameters (
        id bigint GENERATED ALWAYS AS IDENTITY,
        key character varying(200) NOT NULL,
        value text NOT NULL,
        description text NOT NULL,
        data_type character varying(20) NOT NULL,
        validation_pattern text,
        is_sensitive boolean NOT NULL,
        revision integer NOT NULL,
        updated_by uuid,
        external_id uuid NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        CONSTRAINT pk_parameters PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023004421_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_parameter_history_external_id ON settings.parameter_history (external_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023004421_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_parameter_history_parameter_key_revision ON settings.parameter_history (parameter_key, revision);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023004421_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_parameters_external_id ON settings.parameters (external_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023004421_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_parameters_key ON settings.parameters (key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023004421_InitialCreate') THEN
    INSERT INTO settings.parameters
        (external_id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by, created_at, updated_at)
    VALUES
        (uuidv7(), 'Identity:AdminInvitations:AllowedEmailDomain', 'adnerscarpelini.com',
         'Domínio de e-mail obrigatório para contas de administrador. Usado na criação do primeiro administrador por comando de linha (#11), no convite de administradores (#25) e na validação dos aprovadores de convites (#36). Formato: apenas o domínio, sem @, em minúsculas (por exemplo, adnerscarpelini.com). A comparação é exata e não aceita subdomínios. Alterar este valor não afeta administradores existentes, apenas novas criações e convites.',
         'Domain', NULL, true, 0, NULL, now(), now()),
        (uuidv7(), 'Identity:AdminInvitations:ApproverEmails', 'oi@adnerscarpelini.com',
         'E-mails dos administradores que aprovam convites de novos administradores (#36), separados por vírgula. Cada e-mail precisa pertencer a um administrador ativo e ao domínio de Identity:AdminInvitations:AllowedEmailDomain; quem cria um convite nunca pode aprová-lo. Alterar este valor muda quem recebe e pode decidir os convites pendentes a partir da próxima leitura.',
         'EmailList', NULL, true, 0, NULL, now(), now());

    INSERT INTO settings.parameter_history
        (external_id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
         data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at)
    SELECT uuidv7(), key, 0, NULL, value, NULL, description,
           data_type, validation_pattern, is_sensitive, NULL, now(), now(), now()
    FROM settings.parameters;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023004421_InitialCreate') THEN
    INSERT INTO settings."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261023004421_InitialCreate', '10.0.13');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023045648_AddLockoutParameters') THEN
    INSERT INTO settings.parameters
        (external_id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by, created_at, updated_at)
    VALUES
        (uuidv7(), 'Identity:Lockout:MaxFailedAttempts', '5',
         'Quantidade de falhas de senha seguidas que bloqueia a conta no login (#13). Ao atingir o limite, a conta fica bloqueada pelo tempo de Identity:Lockout:DurationMinutes e o contador volta a zero; um login bem-sucedido antes do limite também zera o contador. Formato: inteiro de 1 a 99. Alterar este valor vale a partir da próxima falha de senha e não libera contas já bloqueadas.',
         'Integer', '[1-9][0-9]?', false, 0, NULL, now(), now()),
        (uuidv7(), 'Identity:Lockout:DurationMinutes', '15',
         'Duração, em minutos, do bloqueio da conta após Identity:Lockout:MaxFailedAttempts falhas de senha seguidas (#13). Durante o bloqueio o login responde o mesmo 401 de senha errada, mesmo com a senha correta. O bloqueio expira sozinho. Formato: inteiro de 1 a 1440. Alterar este valor vale para os próximos bloqueios e não muda o fim dos bloqueios em andamento.',
         'Integer', '[1-9][0-9]{0,2}|1[0-3][0-9]{2}|14[0-3][0-9]|1440', false, 0, NULL, now(), now());

    INSERT INTO settings.parameter_history
        (external_id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
         data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at)
    SELECT uuidv7(), key, 0, NULL, value, NULL, description,
           data_type, validation_pattern, is_sensitive, NULL, now(), now(), now()
    FROM settings.parameters
    WHERE key IN ('Identity:Lockout:MaxFailedAttempts', 'Identity:Lockout:DurationMinutes');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023045648_AddLockoutParameters') THEN
    INSERT INTO settings."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261023045648_AddLockoutParameters', '10.0.13');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023094937_AddRegistrationParameters') THEN
    INSERT INTO settings.parameters
        (external_id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by, created_at, updated_at)
    VALUES
        (uuidv7(), 'Identity:Registration:TokenLifetimeHours', '24',
         'Validade, em horas, do link de cadastro de cliente enviado por e-mail (#20). Depois desse prazo o link não conclui o cadastro (#21) e a pessoa precisa solicitar de novo. Formato: inteiro de 1 a 168. Alterar este valor vale para os próximos links; os já enviados mantêm a validade com que foram gerados.',
         'Integer', '[1-9]|[1-9][0-9]|1[0-5][0-9]|16[0-8]', false, 0, NULL, now(), now()),
        (uuidv7(), 'Identity:Registration:CompletionUrl', 'http://localhost:3000/cadastro/concluir',
         'Endereço da tela que conclui o cadastro de cliente, usado no link enviado por e-mail (#20). O sistema acrescenta ?token=<token> ao final, então o valor não pode ter query string nem fragmento. Formato: URL http ou https sem espaços, ? ou #. Alterar este valor vale para os próximos e-mails.',
         'String', 'https?://[^\s?#]+', false, 0, NULL, now(), now());

    INSERT INTO settings.parameter_history
        (external_id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
         data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at)
    SELECT uuidv7(), key, 0, NULL, value, NULL, description,
           data_type, validation_pattern, is_sensitive, NULL, now(), now(), now()
    FROM settings.parameters
    WHERE key IN ('Identity:Registration:TokenLifetimeHours', 'Identity:Registration:CompletionUrl');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023094937_AddRegistrationParameters') THEN
    INSERT INTO settings."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261023094937_AddRegistrationParameters', '10.0.13');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023102508_AddPasswordResetParameters') THEN
    INSERT INTO settings.parameters
        (external_id, key, value, description, data_type, validation_pattern, is_sensitive, revision, updated_by, created_at, updated_at)
    VALUES
        (uuidv7(), 'Identity:PasswordReset:TokenLifetimeMinutes', '60',
         'Validade, em minutos, do link de redefinição de senha enviado por e-mail (#22). Depois desse prazo o link não redefine a senha (#23) e a pessoa precisa pedir de novo. Formato: inteiro de 1 a 1440. Alterar este valor vale para os próximos links; os já enviados mantêm a validade com que foram gerados.',
         'Integer', '[1-9][0-9]{0,2}|1[0-3][0-9]{2}|14[0-3][0-9]|1440', false, 0, NULL, now(), now()),
        (uuidv7(), 'Identity:PasswordReset:CompletionUrl', 'http://localhost:3000/senha/redefinir',
         'Endereço da tela que redefine a senha, usado no link enviado por e-mail (#22). O sistema acrescenta ?token=<token> ao final, então o valor não pode ter query string nem fragmento. Formato: URL http ou https sem espaços, ? ou #. Alterar este valor vale para os próximos e-mails.',
         'String', 'https?://[^\s?#]+', false, 0, NULL, now(), now());

    INSERT INTO settings.parameter_history
        (external_id, parameter_key, revision, previous_value, new_value, previous_description, new_description,
         data_type, validation_pattern, is_sensitive, changed_by, changed_at, created_at, updated_at)
    SELECT uuidv7(), key, 0, NULL, value, NULL, description,
           data_type, validation_pattern, is_sensitive, NULL, now(), now(), now()
    FROM settings.parameters
    WHERE key IN ('Identity:PasswordReset:TokenLifetimeMinutes', 'Identity:PasswordReset:CompletionUrl');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM settings."__EFMigrationsHistory" WHERE "MigrationId" = '20261023102508_AddPasswordResetParameters') THEN
    INSERT INTO settings."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261023102508_AddPasswordResetParameters', '10.0.13');
    END IF;
END $EF$;
COMMIT;

