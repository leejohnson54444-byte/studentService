using AutoMapper;
using MongoDB.Bson;
using StudentService.Domain.DTOs;
using StudentService.Infrastructure.Models;

namespace StudentService.Infrastructure.Profiles
{
	public class ApplicationMappingProfile : Profile
	{
		public ApplicationMappingProfile()
		{
			CreateMap<Application, ApplicationInfo>()
				.ForMember(dest => dest.ApplicationId, opt => opt.MapFrom(src => src.ApplicationId.ToString()))
				.ForMember(dest => dest.Status, opt => opt.MapFrom(src => src.Status))
				.ForMember(dest => dest.StudentBasicInfo, opt => opt.Ignore())
				.ForMember(dest => dest.JobBasicInfo, opt => opt.Ignore());

			CreateMap<ApplicationCreate, Application>()
				.ForMember(dest => dest.ApplicationId, opt => opt.MapFrom(_ => ObjectId.GenerateNewId()))
				.ForMember(dest => dest.StudentId, opt => opt.MapFrom(src => ObjectId.Parse(src.StudentId)))
				.ForMember(dest => dest.JobId, opt => opt.MapFrom(src => ObjectId.Parse(src.JobId)))
				.ForMember(dest => dest.Status, opt => opt.MapFrom(_ => "pending"));
		}
	}
}
