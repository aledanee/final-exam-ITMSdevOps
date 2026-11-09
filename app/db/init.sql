-- Runs automatically when the MySQL data directory is first initialized.
CREATE DATABASE IF NOT EXISTS taskdb
  CHARACTER SET utf8mb4
  COLLATE utf8mb4_unicode_ci;

CREATE USER IF NOT EXISTS 'taskuser'@'%'
  IDENTIFIED BY 'taskpass';

GRANT ALL PRIVILEGES ON taskdb.* TO 'taskuser'@'%';
FLUSH PRIVILEGES;

USE taskdb;

CREATE TABLE IF NOT EXISTS Tasks (
    Id          INT NOT NULL AUTO_INCREMENT,
    Title       VARCHAR(200) NOT NULL,
    Description VARCHAR(2000) NOT NULL,
    IsCompleted TINYINT(1) NOT NULL DEFAULT 0,
    CreatedAt   DATETIME(6) NOT NULL,
    PRIMARY KEY (Id),
    INDEX IX_Tasks_IsCompleted (IsCompleted)
) ENGINE = InnoDB;
