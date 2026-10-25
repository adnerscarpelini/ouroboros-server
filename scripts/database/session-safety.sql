-- Incluído depois de cada conexão (\connect abre outra sessão e descarta os SET da anterior).
-- As senhas passam por instruções SQL: nada disso pode ir para o log do servidor, nem em erro nem
-- por configuração de instância (log_statement, log_min_duration_statement).
SET log_statement = 'none';
SET log_min_error_statement = 'panic';
SET log_min_duration_statement = -1;
SET log_min_duration_sample = -1;
SET log_statement_sample_rate = 0;
