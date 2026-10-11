-- Historical Expense labels confirmed by José for issue #129.
-- One-time mirror seed only; Sheets retains category authority.
INSERT INTO categories (name) VALUES ('Startup'), ('Wellness')
ON CONFLICT (name) DO NOTHING;
