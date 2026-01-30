// MongoDB Initialization Script for Distributed System
// This script sets up indexes and shard keys for optimal performance

// Switch to the database
db = db.getSiblingDB('student_service_DB');

// Create indexes for better query performance and sharding preparation

// Students collection indexes
db.students.createIndex({ "StudentId": 1 }, { unique: true });
db.students.createIndex({ "Faculty": 1, "YearOfStudy": 1 });
db.students.createIndex({ "Traits.Name": 1 });
db.students.createIndex({ "CreatedAt": -1 });

// Jobs collection indexes  
db.jobs.createIndex({ "JobId": 1 }, { unique: true });
db.jobs.createIndex({ "CompanyId": 1 });
db.jobs.createIndex({ "JobType": 1 });
db.jobs.createIndex({ "StartDate": 1, "EndDate": 1 });
db.jobs.createIndex({ "Title": "text", "Description": "text" }); // Text search index
db.jobs.createIndex({ "CreatedAt": -1 });

// Applications collection indexes
db.applications.createIndex({ "ApplicationId": 1 }, { unique: true });
db.applications.createIndex({ "StudentId": 1, "JobId": 1 }, { unique: true }); // Compound unique index
db.applications.createIndex({ "JobId": 1, "Status": 1 });
db.applications.createIndex({ "StudentId": 1, "CreatedAt": -1 });
db.applications.createIndex({ "Status": 1 });

// Companies collection indexes
db.companies.createIndex({ "CompanyId": 1 }, { unique: true });
db.companies.createIndex({ "Name": 1 });

print("Indexes created successfully!");

// Note: To enable sharding in a production MongoDB cluster, run these commands on mongos:
// 
// sh.enableSharding("student_service_DB");
// 
// Shard by CompanyId for jobs (range-based sharding for company queries)
// sh.shardCollection("student_service_DB.jobs", { "CompanyId": "hashed" });
// 
// Shard by StudentId for applications (hashed for even distribution)
// sh.shardCollection("student_service_DB.applications", { "StudentId": "hashed" });
// 
// Shard by StudentId for students (hashed for even distribution)
// sh.shardCollection("student_service_DB.students", { "StudentId": "hashed" });
