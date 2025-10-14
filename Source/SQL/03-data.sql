/*
    Insert some "pre-existing" data
*/
insert into dbo.TrainingSessions 
    (RecordedOn, [Type], Steps, Distance, Duration, Calories)
values 
    ('20211028 17:27:23 -08:00', 'Run', 3784, 5123, 32*60+3, 526),
    ('20211027 17:54:48 -08:00', 'Run', 0, 4981, 32*60+37, 480)
go

/*
    View Data
*/
select * from dbo.TrainingSessions
go

/*
    Make some changes
*/
insert into dbo.TrainingSessions 
    (RecordedOn, [Type], Steps, Distance, Duration, Calories)
values 
    ('20211026 18:24:32 -08:00', 'Run', 4866, 4562, 30*60+18, 475)
go

/*
    Make some changes
*/
insert into dbo.TrainingSessions 
    (RecordedOn, [Type], Steps, Distance, Duration, Calories)
values 
    ('20211026 18:24:32 -08:00', 'Run', 4866, 4562, 30*60+18, 475)
go

update 
    dbo.TrainingSessions 
set 
    Steps = Steps+1
where 
    Id = (SELECT MAX(ID) FROM TrainingSessions) -- Make sure to select an if returned by previous rows

/*
    Delete someting
*/
delete s from dbo.TrainingSessions s where Id = (SELECT MAX(ID) FROM TrainingSessions) -- Make sure to select an if returned by previous rows
go